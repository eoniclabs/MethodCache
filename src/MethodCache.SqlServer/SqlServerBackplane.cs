using System.Data;
using System.Globalization;
using System.Text;
using MethodCache.Core.Storage.Abstractions;
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

    // A larger jump is SQL Server's identity cache skipping values after a restart, not rows still committing
    private const int MaxGapRange = 100;
    private const int MaxGaps = 1000;
    private static readonly TimeSpan GapLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstPollMargin = TimeSpan.FromSeconds(1);

    private readonly SqlServerOptions _options;
    private readonly ILogger _logger;
    private readonly Dictionary<long, DateTime> _gaps = new();
    private CancellationTokenSource? _polling;
    private Task? _loop;
    private long? _lastId;
    private DateTime _lastPolled;
    private DateTime _nextCleanup;
    private bool _failing;
    private System.Diagnostics.Stopwatch _subscribedAt = new();

    public SqlServerBackplane(SqlServerOptions options, ILogger<SqlServerBackplane>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
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

        _subscribedAt = System.Diagnostics.Stopwatch.StartNew();
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
        var sql = $"INSERT INTO {_options.QualifiedInvalidationsTableName} ([InstanceId], [MessageType], [Target]) VALUES (@instanceId, @type, @target);";

        var connection = new SqlConnection(_options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@instanceId", SqlDbType.VarChar, 32) { Value = InstanceId });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.TinyInt) { Value = (byte)type });
        command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, -1) { Value = target });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The last id written more than <paramref name="age"/> ago, by the database's clock.</summary>
    private async Task<long> ReadLastIdAsync(TimeSpan age, CancellationToken cancellationToken)
    {
        var sql = $"SELECT ISNULL(MAX([Id]), 0) FROM {_options.QualifiedInvalidationsTableName} WHERE [CreatedAt] < DATEADD(millisecond, -@age, SYSUTCDATETIME());";

        var connection = new SqlConnection(_options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = new SqlCommand(sql, connection);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.Add(new SqlParameter("@age", SqlDbType.Int) { Value = (int)Math.Min(int.MaxValue, age.TotalMilliseconds) });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async Task PollAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.BackplanePollingInterval);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
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
        var now = DateTime.UtcNow;
        if (_lastId == null || now - _lastPolled >= _options.BackplaneRetention / 2)
        {
            // First poll: start from the invalidations written before this instance subscribed, with a margin, since
            // it may have cached values since then. After a longer outage, rows this instance never read may
            // already be deleted, so it drops everything it holds in memory and starts from now.
            var outage = _lastId != null;
            var age = outage ? TimeSpan.Zero : _subscribedAt.Elapsed + FirstPollMargin;
            _lastId = await ReadLastIdAsync(age, cancellationToken).ConfigureAwait(false);
            _gaps.Clear();
            _lastPolled = now;
            if (outage)
            {
                await onMessage(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }).ConfigureAwait(false);
            }

            return;
        }

        for (var batch = 0; batch < MaxBatchesPerPoll; batch++)
        {
            var (messages, newRows) = await ReadAsync(now, cancellationToken).ConfigureAwait(false);
            foreach (var message in messages)
            {
                await onMessage(message).ConfigureAwait(false);
            }

            if (newRows < BatchSize)
            {
                break;
            }
        }

        _lastPolled = now;
        await CleanupAsync(now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads rows after the last id, and rows that filled earlier gaps. Returns how many new rows came.</summary>
    private async Task<(List<BackplaneMessage> Messages, int NewRows)> ReadAsync(DateTime now, CancellationToken cancellationToken)
    {
        foreach (var expired in _gaps.Where(gap => now - gap.Value > GapLifetime).Select(gap => gap.Key).ToList())
        {
            _gaps.Remove(expired);
        }

        var gapIds = _gaps.Keys.ToList();
        var sql = new StringBuilder($"""
            SELECT TOP (@batch) [Id], [InstanceId], [MessageType], [Target], [CreatedAt]
            FROM {_options.QualifiedInvalidationsTableName}
            WHERE [Id] > @lastId
            ORDER BY [Id];
            """);
        if (gapIds.Count > 0)
        {
            sql.Append($"""

                SELECT [Id], [InstanceId], [MessageType], [Target], [CreatedAt]
                FROM {_options.QualifiedInvalidationsTableName}
                WHERE [Id] IN ({string.Join(", ", gapIds.Select((_, i) => "@gap" + i.ToString(CultureInfo.InvariantCulture)))});
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
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = BatchSize });
            command.Parameters.Add(new SqlParameter("@lastId", SqlDbType.BigInt) { Value = lastId });
            for (var i = 0; i < gapIds.Count; i++)
            {
                command.Parameters.Add(new SqlParameter("@gap" + i.ToString(CultureInfo.InvariantCulture), SqlDbType.BigInt) { Value = gapIds[i] });
            }

            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);

            var expected = lastId + 1;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                newRows++;
                if (id > expected && id - expected <= MaxGapRange)
                {
                    for (var missing = expected; missing < id && _gaps.Count < MaxGaps; missing++)
                    {
                        _gaps[missing] = now;
                    }
                }

                expected = id + 1;
                lastId = id;
                AddMessage(reader, messages);
            }

            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    _gaps.Remove(reader.GetInt64(0));
                    AddMessage(reader, messages);
                }
            }
        }

        _lastId = lastId;
        return (messages, newRows);
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
        var sql = $"DELETE TOP (@batch) FROM {_options.QualifiedInvalidationsTableName} WHERE [CreatedAt] < DATEADD(second, -@retention, SYSUTCDATETIME());";

        var connection = new SqlConnection(_options.ConnectionString);
        await using var connectionScope = connection.ConfigureAwait(false);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Small batches keep locks short; repeat until the table is caught up, however fast rows are written
        for (var batch = 0; batch < MaxCleanupBatches; batch++)
        {
            var command = new SqlCommand(sql, connection);
            await using var commandScope = command.ConfigureAwait(false);
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = CleanupBatchSize });
            command.Parameters.Add(new SqlParameter("@retention", SqlDbType.Int) { Value = (int)Math.Min(int.MaxValue, _options.BackplaneRetention.TotalSeconds) });
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) < CleanupBatchSize)
            {
                return;
            }
        }
    }
}
