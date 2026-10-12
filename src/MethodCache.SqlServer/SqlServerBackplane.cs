using System.Buffers.Binary;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MethodCache.Extensibility;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MethodCache.SqlServer;

/// <summary>
/// Sends cache invalidations between instances through a SQL Server table that every instance polls.
/// </summary>
/// <remarks>
/// Identity values are handed out before transactions commit, so a row can become visible after a row with a
/// higher id was already read. Ids skipped that way are remembered as gaps and looked up again for a few seconds.
/// When this instance could not poll for longer than the table keeps rows, it may have missed invalidations, so it
/// clears its in-memory cache.
/// </remarks>
internal sealed class SqlServerBackplane : IBackplane, IAsyncDisposable, IDisposable
{
    private const int BatchSize = 1000;
    private const int MaxBatchesPerPoll = 10;
    private const int CleanupBatchSize = 5000;
    private const int MaxCleanupBatches = 20;

    // Polls a message whose handler keeps failing is retried before it is replaced by a clear, so it can't hold back
    // every later invalidation until the outage clear after half the retention
    internal const int MaxDeliveryAttempts = 5;

    // Identity values skipped by rows that may still commit, watched for a few seconds. Ranges, so a large jump
    // (many concurrent inserts, or the identity cache after a restart) costs one range query, not one id each.
    private const int MaxGapRanges = 100;
    private static readonly TimeSpan GapLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstPollMargin = TimeSpan.FromSeconds(1);

    private readonly SqlServerOptions _options;
    private readonly ILogger _logger;
    private readonly byte[] _scope;
    private readonly TimeProvider _timeProvider;
    private readonly List<GapRange> _gaps = new();
    private CancellationTokenSource? _polling;
    private Task? _loop;
    private long? _lastId;
    private PendingBatch? _pendingBatch;
    private DateTime _lastPolled;
    private DateTime _nextCleanup;
    private bool _failing;
    private long _subscribedAt;

    public SqlServerBackplane(SqlServerOptions options, ILogger<SqlServerBackplane>? logger = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _scope = GetScope(options.KeyPrefix);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _subscribedAt = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.KeyInvalidation, key, cancellationToken);

    /// <inheritdoc />
    public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.TagInvalidation, tag, cancellationToken);

    /// <inheritdoc />
    public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        if (_polling != null)
        {
            throw new InvalidOperationException("The backplane already has a subscriber.");
        }

        _subscribedAt = _timeProvider.GetTimestamp();
        _polling = new CancellationTokenSource();
        _loop = PollAsync(onMessage, _polling.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        var polling = Interlocked.Exchange(ref _polling, null);
        if (polling == null)
        {
            return;
        }

        await polling.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_loop != null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            polling.Dispose();
            _loop = null;
        }
    }

    public ValueTask DisposeAsync() => new(UnsubscribeAsync());

    public void Dispose()
    {
        // Stops polling without waiting for a poll in progress to finish
        var polling = Interlocked.Exchange(ref _polling, null);
        if (polling != null)
        {
            polling.Cancel();
            polling.Dispose();
        }
    }

    private async Task PublishAsync(BackplaneMessageType type, string target, CancellationToken cancellationToken)
    {
        var sql = $"INSERT INTO {_options.QualifiedInvalidationsTableName} ([Scope], [InstanceId], [MessageType], [Target]) VALUES (@scope, @instanceId, @type, @target);";

        var connection = new SqlConnection(_options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        AddScope(command);
        command.Parameters.Add(new SqlParameter("@instanceId", SqlDbType.VarChar, 32) { Value = InstanceId });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.TinyInt) { Value = (byte)type });
        command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, -1) { Value = target });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The last id written more than <paramref name="age"/> ago, by the database's clock.</summary>
    private async Task<long> ReadLastIdAsync(TimeSpan age, CancellationToken cancellationToken)
    {
        var sql = $"SELECT ISNULL(MAX([Id]), 0) FROM {_options.QualifiedInvalidationsTableName} WHERE [Scope] = @scope AND [CreatedAt] < DATEADD(millisecond, -@age, SYSUTCDATETIME());";

        var connection = new SqlConnection(_options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        AddScope(command);
        command.Parameters.Add(new SqlParameter("@age", SqlDbType.Int) { Value = (int)Math.Min(int.MaxValue, age.TotalMilliseconds) });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task PollAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.BackplanePollingInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await PollOnceAsync(onMessage, cancellationToken).ConfigureAwait(false);
                if (_failing)
                {
                    _failing = false;
                    _logger.LogInformation("The MethodCache backplane reads invalidations from SQL Server again.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The rows stay in the table, so the next poll picks up whatever this one missed
                if (!_failing)
                {
                    _failing = true;
                    _logger.LogWarning(ex, "The MethodCache backplane could not read invalidations from SQL Server; retrying every {Interval}.", _options.BackplanePollingInterval);
                }
            }
        }
    }

    internal async Task PollOnceAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (_lastId == null || now - _lastPolled >= _options.BackplaneRetention / 2)
        {
            // First poll: start from the invalidations written before this instance subscribed, with a margin, since
            // it may have cached values since then. After a longer outage, rows this instance never read may
            // already be deleted, so it drops everything it holds in memory and starts from now.
            var outage = _lastId != null;
            var age = outage ? TimeSpan.Zero : _timeProvider.GetElapsedTime(_subscribedAt) + FirstPollMargin;
            var lastId = await ReadLastIdAsync(age, cancellationToken).ConfigureAwait(false);
            if (outage)
            {
                await onMessage(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }).ConfigureAwait(false);
            }

            // A failed recovery clear must be attempted again; none of its state is acknowledged beforehand.
            _lastId = lastId;
            _gaps.Clear();
            _pendingBatch = null;
            _lastPolled = now;
            return;
        }

        for (var batch = 0; batch < MaxBatchesPerPoll; batch++)
        {
            var pending = _pendingBatch ??= await ReadAsync(now, cancellationToken).ConfigureAwait(false);
            while (pending.DeliveredCount < pending.Messages.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DeliverAsync(onMessage, pending, cancellationToken).ConfigureAwait(false);
                pending.DeliveredCount++;
                pending.FailedAttempts = 0;
            }

            _lastId = pending.LastId;
            _gaps.Clear();
            _gaps.AddRange(pending.Gaps);
            _pendingBatch = null;
            if (pending.NewRows < BatchSize)
            {
                break;
            }
        }

        _lastPolled = now;
        await CleanupAsync(now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers the next message of <paramref name="pending"/>. After <see cref="MaxDeliveryAttempts"/> failed polls
    /// it gives up on that message and clears the subscriber's memory instead, since skipping an invalidation would
    /// leave the value it removes behind. A failed clear throws, so it is tried again on the next poll.
    /// </summary>
    private async Task DeliverAsync(Func<BackplaneMessage, Task> onMessage, PendingBatch pending, CancellationToken cancellationToken)
    {
        var message = pending.Messages[pending.DeliveredCount];
        if (pending.FailedAttempts < MaxDeliveryAttempts)
        {
            try
            {
                await onMessage(message).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && ++pending.FailedAttempts < MaxDeliveryAttempts)
            {
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "The MethodCache backplane could not apply a {Type} invalidation after {Attempts} attempts; it clears this instance's in-memory cache instead.", message.Type, MaxDeliveryAttempts);
            }
        }

        await onMessage(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }).ConfigureAwait(false);
    }

    /// <summary>Reads rows after the last id, and rows that filled earlier gaps. Returns how many new rows came.</summary>
    private async Task<PendingBatch> ReadAsync(DateTime now, CancellationToken cancellationToken)
    {
        // All discovery state is staged. A query or callback failure cannot consume a cursor or a gap id.
        var gaps = _gaps.Where(gap => now - gap.SeenAt <= GapLifetime)
            .Select(gap => gap with { Delivered = new HashSet<long>(gap.Delivered) }).ToList();
        var nextGaps = new List<GapRange>(gaps);
        var sql = new StringBuilder($"""
            SELECT TOP (@batch) [Id], [InstanceId], [MessageType], [Target], [CreatedAt]
            FROM {_options.QualifiedInvalidationsTableName}
            WHERE [Scope] = @scope AND [Id] > @lastId
            ORDER BY [Id];
            """);
        if (gaps.Count > 0)
        {
            sql.Append($"""

                SELECT [Id], [InstanceId], [MessageType], [Target], [CreatedAt]
                FROM {_options.QualifiedInvalidationsTableName}
                WHERE [Scope] = @scope AND ({string.Join(" OR ", gaps.Select((_, i) => $"[Id] BETWEEN @gapFrom{i.ToString(CultureInfo.InvariantCulture)} AND @gapTo{i.ToString(CultureInfo.InvariantCulture)}"))});
                """);
        }

        var messages = new List<BackplaneMessage>();
        var newRows = 0;
        var lastId = _lastId!.Value;
        var connection = new SqlConnection(_options.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = new SqlCommand(sql.ToString(), connection);
            await using var commandScope = command.ConfigureAwait(false);
            AddScope(command);
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = BatchSize });
            command.Parameters.Add(new SqlParameter("@lastId", SqlDbType.BigInt) { Value = lastId });
            for (var i = 0; i < gaps.Count; i++)
            {
                var index = i.ToString(CultureInfo.InvariantCulture);
                command.Parameters.Add(new SqlParameter("@gapFrom" + index, SqlDbType.BigInt) { Value = gaps[i].From });
                command.Parameters.Add(new SqlParameter("@gapTo" + index, SqlDbType.BigInt) { Value = gaps[i].To });
            }

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);

            var expected = lastId + 1;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                newRows++;
                if (id > expected)
                {
                    AddGap(nextGaps, expected, id - 1, now);
                }

                expected = id + 1;
                lastId = id;
                AddMessage(reader, messages);
            }

            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetInt64(0);
                    if (gaps.Find(gap => gap.From <= id && id <= gap.To) is { } gap && gap.Delivered.Add(id))
                    {
                        AddMessage(reader, messages);
                    }
                }
            }
        }

        return new PendingBatch(messages, newRows, lastId, nextGaps);
    }

    private static void AddGap(List<GapRange> gaps, long from, long to, DateTime now)
    {
        if (gaps.Count < MaxGapRanges)
        {
            gaps.Add(new GapRange(from, to, now));
            return;
        }

        // Too many: widen the newest range to cover this one too. Rows read normally in between may then be
        // delivered twice, which an invalidation tolerates.
        var newest = gaps[^1];
        gaps[^1] = new GapRange(newest.From, to, now) { Delivered = newest.Delivered };
    }

    private sealed record PendingBatch(List<BackplaneMessage> Messages, int NewRows, long LastId, List<GapRange> Gaps)
    {
        public int DeliveredCount { get; set; }

        /// <summary>Polls in which the message at <see cref="DeliveredCount"/> failed.</summary>
        public int FailedAttempts { get; set; }
    }

    /// <summary>Identity values <see cref="From"/> to <see cref="To"/> skipped by rows that may still commit.</summary>
    private sealed record GapRange(long From, long To, DateTime SeenAt)
    {
        public HashSet<long> Delivered { get; init; } = new();
    }

    private void AddMessage(SqlDataReader reader, List<BackplaneMessage> messages)
    {
        var instanceId = reader.GetString(1);
        if (instanceId == InstanceId)
        {
            return;
        }

        var message = CreateMessage((BackplaneMessageType)reader.GetByte(2), reader.GetString(3), instanceId, reader.GetDateTime(4));
        if (message != null)
        {
            messages.Add(message);
        }
    }

    internal static BackplaneMessage? CreateMessage(BackplaneMessageType type, string target, string instanceId, DateTime createdAt)
    {
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));
        return type switch
        {
            BackplaneMessageType.KeyInvalidation => new BackplaneMessage { Type = type, Key = target, InstanceId = instanceId, Timestamp = timestamp },
            BackplaneMessageType.TagInvalidation => new BackplaneMessage { Type = type, Tag = target, InstanceId = instanceId, Timestamp = timestamp },
            _ => null
        };
    }

    private async Task CleanupAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now + CleanupInterval;
        var sql = $"DELETE TOP (@batch) FROM {_options.QualifiedInvalidationsTableName} WHERE [Scope] = @scope AND [CreatedAt] < DATEADD(second, -@retention, SYSUTCDATETIME());";

        var connection = new SqlConnection(_options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Small batches keep locks short; repeat until the table is caught up, however fast rows are written
        for (var batch = 0; batch < MaxCleanupBatches; batch++)
        {
            var command = new SqlCommand(sql, connection);
            await using var commandScope = command.ConfigureAwait(false);
            AddScope(command);
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = CleanupBatchSize });
            command.Parameters.Add(new SqlParameter("@retention", SqlDbType.Int) { Value = (int)Math.Min(int.MaxValue, _options.BackplaneRetention.TotalSeconds) });
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) < CleanupBatchSize)
            {
                return;
            }
        }
    }

    private void AddScope(SqlCommand command) => command.Parameters.Add(new SqlParameter("@scope", SqlDbType.Binary, 32) { Value = _scope });

    internal static byte[] GetScope(string prefix)
    {
        // Hash exact UTF-16 code units in a fixed byte order: casing, normalization and even unpaired
        // surrogates stay distinct, independently of SQL collation and text encoder fallback.
        var bytes = new byte[checked(prefix.Length * 2)];
        for (var i = 0; i < prefix.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), prefix[i]);
        return SHA256.HashData(bytes);
    }
}
