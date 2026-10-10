using System.Data;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Data.SqlClient;

namespace MethodCache.Providers.SqlServer;

/// <summary>
/// Sends cache invalidations between instances through a SQL Server table that every instance polls.
/// </summary>
/// <remarks>
/// Identity values are handed out before transactions commit, so a row can become visible after a row with a
/// higher id was already read. Each poll therefore also re-reads the last few seconds of rows and skips the ids
/// it has already handled.
/// </remarks>
internal sealed class SqlServerBackplane : IBackplane, IAsyncDisposable, IDisposable
{
    private const int BatchSize = 1000;
    private static readonly TimeSpan Lookback = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    private readonly SqlServerOptions _options;
    private HashSet<long> _handled = new();
    private CancellationTokenSource? _polling;
    private Task? _loop;
    private long? _lastId;
    private DateTime _nextCleanup;

    public SqlServerBackplane(SqlServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@instanceId", SqlDbType.VarChar, 32) { Value = InstanceId });
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.TinyInt) { Value = (byte)type });
        command.Parameters.Add(new SqlParameter("@target", SqlDbType.NVarChar, -1) { Value = target });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ReadMaxIdAsync(CancellationToken cancellationToken)
    {
        var sql = $"SELECT ISNULL(MAX([Id]), 0) FROM {_options.QualifiedInvalidationsTableName};";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
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
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The rows stay in the table, so the next poll picks up whatever this one missed. If the database
                // is unreachable for longer than BackplaneRetention, entries may be stale until they expire.
            }
        }
    }

    internal async Task PollOnceAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        if (_lastId is not { } lastId)
        {
            // Start from what is in the table now: older invalidations happened before this instance cached
            // anything. This runs on the first successful poll, so a database that is down or a table that is
            // not created yet at startup only delays it.
            _lastId = await ReadMaxIdAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var sql = $"""
            SELECT TOP (@batch) [Id], [InstanceId], [MessageType], [Target], [CreatedAt]
            FROM {_options.QualifiedInvalidationsTableName}
            WHERE [Id] > @lastId OR [CreatedAt] >= DATEADD(millisecond, -@lookback, SYSUTCDATETIME())
            ORDER BY [Id];
            """;

        var messages = new List<BackplaneMessage>();
        var returned = new HashSet<long>();
        await using (var connection = new SqlConnection(_options.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = BatchSize });
            command.Parameters.Add(new SqlParameter("@lastId", SqlDbType.BigInt) { Value = lastId });
            command.Parameters.Add(new SqlParameter("@lookback", SqlDbType.Int) { Value = (int)Lookback.TotalMilliseconds });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);
                var createdAt = reader.GetDateTime(4);
                if (id > lastId)
                {
                    lastId = id;
                }

                returned.Add(id);
                if (_handled.Contains(id))
                {
                    continue;
                }

                var instanceId = reader.GetString(1);
                if (instanceId == InstanceId)
                {
                    continue;
                }

                var message = CreateMessage((BackplaneMessageType)reader.GetByte(2), reader.GetString(3), instanceId, createdAt);
                if (message != null)
                {
                    messages.Add(message);
                }
            }
        }

        _lastId = lastId;
        foreach (var message in messages)
        {
            await onMessage(message).ConfigureAwait(false);
        }

        // A row that a complete read no longer returns has left the lookback window for good, so its id need not
        // be remembered. A full batch may have cut off rows, so keep everything until a read is complete.
        if (returned.Count < BatchSize)
        {
            _handled = returned;
        }
        else
        {
            _handled.UnionWith(returned);
        }

        await CleanupAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now + CleanupInterval;
        var sql = $"DELETE TOP (@batch) FROM {_options.QualifiedInvalidationsTableName} WHERE [CreatedAt] < DATEADD(second, -@retention, SYSUTCDATETIME());";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(new SqlParameter("@batch", SqlDbType.Int) { Value = BatchSize });
        command.Parameters.Add(new SqlParameter("@retention", SqlDbType.Int) { Value = (int)Math.Min(int.MaxValue, _options.BackplaneRetention.TotalSeconds) });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
