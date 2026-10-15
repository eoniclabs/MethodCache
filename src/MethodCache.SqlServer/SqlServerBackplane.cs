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
/// higher id was already read. Skipped ids are remembered as gaps; after five seconds a non-waiting locking
/// probe confirms their absence before they can be forgotten. Locked gaps stay watched without delaying newer
/// invalidations. A failed query leaves them for retry.
/// Ids taken by rows of other prefixes sharing the table are committed, so they are not kept as gaps.
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
    private int _rejectedWarning;
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

    /// <summary>Identity ranges this instance currently watches for rows that may still commit.</summary>
    internal int GapRangeCount => _gaps.Count;

    /// <summary>Identity values in <see cref="GapRangeCount"/> ranges.</summary>
    internal long GapIdCount => _gaps.Sum(gap => gap.To - gap.From + 1);

    /// <inheritdoc />
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.KeyInvalidation, key, cancellationToken);

    /// <inheritdoc />
    public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.TagInvalidation, tag, cancellationToken);

    /// <inheritdoc />
    public Task PublishMethodInvalidationAsync(string methodId, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.MethodInvalidation, methodId, cancellationToken);


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
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.TinyInt) { Value = (byte)MethodCache.Infrastructure.LosslessWireText.ToMessageType(type) });
        command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, -1) { Value = MethodCache.Infrastructure.LosslessWireText.Encode(target) });
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
        // READPAST skips rows that are still committing, as read committed snapshot does, so an open insert never
        // blocks the poll. A skipped row below a later one becomes a gap and arrives once it commits.
        var gaps = _gaps
            .Select(gap => gap with { Delivered = new HashSet<long>(gap.Delivered) }).ToList();
        var sql = $"""
            SELECT TOP (@batch) [Id], [InstanceId], [MessageType], [Target], [CreatedAt]
            FROM {_options.QualifiedInvalidationsTableName} WITH (READPAST)
            WHERE [Scope] = @scope AND [Id] > @lastId
            ORDER BY [Id];
            """;

        var messages = new List<BackplaneMessage>();
        var newRows = 0;
        var lastId = _lastId!.Value;
        var connection = new SqlConnection(_options.ConnectionString);
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = new SqlCommand(sql, connection);
            await using (command.ConfigureAwait(false))
            {
                AddScope(command);
                command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = BatchSize });
                command.Parameters.Add(new SqlParameter("@lastId", SqlDbType.BigInt) { Value = lastId });
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using var readerScope = reader.ConfigureAwait(false);

                var expected = lastId + 1;
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var id = reader.GetInt64(0);
                    newRows++;
                    if (id > expected)
                    {
                        AddGap(gaps, expected, id - 1, now);
                    }

                    expected = id + 1;
                    lastId = id;
                    AddMessage(reader, messages);
                }
            }

            if (gaps.Count > 0)
            {
                gaps = await ReadGapSnapshotAsync(gaps, messages, now,
                    (pending, ready, token) => ReadGapsAsync(connection, pending, ready, token),
                    (old, token) => AreGapsAbsentAsync(connection, old, token), cancellationToken).ConfigureAwait(false);
            }
        }

        return new PendingBatch(messages, newRows, lastId, gaps);
    }

    /// <summary>
    /// Adds this scope's rows that appeared in <paramref name="gaps"/> to <paramref name="messages"/>, and returns
    /// the gaps without the ids that are taken.
    /// </summary>
    /// <remarks>
    /// Applications with other prefixes share the identity column, so most skipped ids belong to their rows. Any
    /// committed row, whatever its scope, proves its id is no longer pending, so only ids absent from the table stay
    /// watched. The ids are read first: a row they show is committed, so the second query returns it if it is ours.
    /// Locked rows are skipped, including in old gaps, so one open transaction cannot hide another gap's committed
    /// invalidation or delay newer messages. Only ids leave the query for other scopes, never their content.
    /// </remarks>
    private async Task<List<GapRange>> ReadGapsAsync(SqlConnection connection, List<GapRange> gaps, List<BackplaneMessage> messages, CancellationToken cancellationToken)
    {
        var ranges = GapPredicate(gaps);
        var sql = $"""
            SELECT MIN([Id]), MAX([Id])
            FROM (
                SELECT [Id], [Id] - ROW_NUMBER() OVER (ORDER BY [Id]) AS [Island]
                FROM {_options.QualifiedInvalidationsTableName} WITH (READPAST)
                WHERE {ranges}) AS [Taken]
            GROUP BY [Island]
            ORDER BY MIN([Id]);

            SELECT [Id], [InstanceId], [MessageType], [Target], [CreatedAt]
            FROM {_options.QualifiedInvalidationsTableName} WITH (READPAST)
            WHERE [Scope] = @scope AND ({ranges});
            """;

        var taken = new List<(long From, long To)>();
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        AddScope(command);
        AddGapParameters(command, gaps);

        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                taken.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }

            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                if (gaps.Find(gap => gap.From <= id && id <= gap.To) is { } gap && gap.Delivered.Add(id))
                {
                    AddMessage(reader, messages);
                }
            }
        }

        return RemoveTaken(gaps, taken);
    }

    /// <summary>Stages gap discovery and retirement separately; transport failures can leave gaps watched without losing ready messages.</summary>
    internal static async Task<List<GapRange>> ReadGapSnapshotAsync(List<GapRange> gaps, List<BackplaneMessage> messages, DateTime now,
        Func<List<GapRange>, List<BackplaneMessage>, CancellationToken, Task<List<GapRange>>> discover,
        Func<List<GapRange>, CancellationToken, Task<bool>> areAbsent, CancellationToken cancellationToken)
    {
        gaps = await discover(gaps, messages, cancellationToken).ConfigureAwait(false);
        var old = gaps.Where(gap => now - gap.SeenAt > GapLifetime).ToList();
        if (old.Count > 0 && await areAbsent(old, cancellationToken).ConfigureAwait(false))
            gaps.RemoveAll(gap => now - gap.SeenAt > GapLifetime);
        return gaps;
    }

    private async Task<bool> AreGapsAbsentAsync(SqlConnection connection, List<GapRange> old, CancellationToken cancellationToken)
    {
        // READPAST already collected committed gap messages. A separate probe may forget only ranges proven
        // absent without skipping locks. NOWAIT turns an open transaction into an immediate retry, never a
        // stalled poll. A row committed since READPAST makes the count nonzero and is delivered next time.
        var sql = $"SELECT COUNT_BIG(*) FROM {_options.QualifiedInvalidationsTableName} WITH (READCOMMITTEDLOCK, NOWAIT) WHERE {GapPredicate(old)};";
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        AddGapParameters(command, old);
        try
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 0;
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested && ex.Number is 1222 or -2)
        {
            // Lock/query timeout affects only retirement; keep every gap and deliver the messages already read.
            _logger.LogDebug(ex, "MethodCache keeps unresolved SQL backplane gaps for another poll.");
            return false;
        }
    }

    private static string GapPredicate(List<GapRange> gaps) => string.Join(" OR ", gaps.Select((_, i) =>
        $"[Id] BETWEEN @gapFrom{i.ToString(CultureInfo.InvariantCulture)} AND @gapTo{i.ToString(CultureInfo.InvariantCulture)}"));

    private static void AddGapParameters(SqlCommand command, List<GapRange> gaps)
    {
        for (var i = 0; i < gaps.Count; i++)
        {
            var index = i.ToString(CultureInfo.InvariantCulture);
            command.Parameters.Add(new SqlParameter("@gapFrom" + index, SqlDbType.BigInt) { Value = gaps[i].From });
            command.Parameters.Add(new SqlParameter("@gapTo" + index, SqlDbType.BigInt) { Value = gaps[i].To });
        }
    }

    /// <summary>Splits <paramref name="gaps"/> around the sorted, disjoint <paramref name="taken"/> ranges.</summary>
    internal static List<GapRange> RemoveTaken(List<GapRange> gaps, List<(long From, long To)> taken)
    {
        var holes = new List<GapRange>();
        var next = 0;
        foreach (var gap in gaps)
        {
            var from = gap.From;
            while (next < taken.Count && taken[next].To < from)
            {
                next++;
            }

            for (var i = next; i < taken.Count && taken[i].From <= gap.To && from <= gap.To; i++)
            {
                if (taken[i].From > from)
                {
                    holes.Add(Piece(gap, from, taken[i].From - 1));
                }

                from = Math.Max(from, taken[i].To + 1);
            }

            if (from <= gap.To)
            {
                holes.Add(Piece(gap, from, gap.To));
            }
        }

        // Many separate holes: widen the newest range over the one before it, as AddGap does
        while (holes.Count > MaxGapRanges)
        {
            var last = holes[^1];
            var previous = holes[^2];
            holes.RemoveAt(holes.Count - 1);
            holes[^1] = new GapRange(previous.From, last.To, previous.SeenAt > last.SeenAt ? previous.SeenAt : last.SeenAt)
            {
                Delivered = new HashSet<long>(previous.Delivered.Concat(last.Delivered))
            };
        }

        return holes;

        static GapRange Piece(GapRange gap, long from, long to) => new(from, to, gap.SeenAt)
        {
            Delivered = new HashSet<long>(gap.Delivered.Where(id => from <= id && id <= to))
        };
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
    internal sealed record GapRange(long From, long To, DateTime SeenAt)
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

        var message = CreateWireMessage(reader.GetByte(2), reader.GetString(3), instanceId, reader.GetDateTime(4));
        if (message == null && Interlocked.Exchange(ref _rejectedWarning, 1) == 0)
            _logger.LogWarning("Rejected an incompatible or malformed MethodCache backplane frame. Invalidations do not cross wire versions; deploy the same version on all nodes.");
        if (message != null)
        {
            messages.Add(message);
        }
    }

    internal static BackplaneMessage? CreateWireMessage(byte type, string wire, string instanceId, DateTime createdAt)
        => MethodCache.Infrastructure.LosslessWireText.TryMessageType(type, out var logical)
            && MethodCache.Infrastructure.LosslessWireText.TryDecode(wire, out var target)
            ? CreateMessage(logical, target, instanceId, createdAt) : null;

    internal static BackplaneMessage? CreateMessage(BackplaneMessageType type, string target, string instanceId, DateTime createdAt)
    {
        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc));
        return type switch
        {
            BackplaneMessageType.KeyInvalidation => new BackplaneMessage { Type = type, Key = target, InstanceId = instanceId, Timestamp = timestamp },
            BackplaneMessageType.MethodInvalidation => new BackplaneMessage { Type = BackplaneMessageType.MethodInvalidation, Key = target, InstanceId = instanceId, Timestamp = timestamp },
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
        // Other prefixes may retain their rows longer, so each instance cleans only its own scope.
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
        var bytes = MethodCache.Infrastructure.LosslessUtf16.GetBytes(prefix);
        return SHA256.HashData(bytes);
    }
}
