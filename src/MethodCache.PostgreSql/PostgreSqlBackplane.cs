using System.Security.Cryptography;
using System.Text;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace MethodCache.PostgreSql;

/// <summary>
/// Sends cache invalidations between instances through a PostgreSQL table. The table is the truth; a
/// <c>NOTIFY</c> only rings the doorbell, so instances read new rows at once instead of at the next poll.
/// </summary>
/// <remarks>
/// <para>
/// Ids come from one cursor row per prefix, updated in the same transaction as the invalidation. Its row lock makes
/// invalidations of one prefix commit in id order, so a reader that has seen an id has seen every smaller one: no
/// gaps to track. The ids follow the database clock in microseconds, so ids handed out after a failover never repeat
/// ids instances already read.
/// </para>
/// <para>
/// Each instance polls every <see cref="PostgreSqlOptions.BackplanePollingInterval"/> as well, so a lost
/// notification, a broken <c>LISTEN</c> connection or a pooler that cannot hold one costs at most one interval. An
/// instance clears its in-memory cache when it could not read the table for half the retention, or when the
/// cursor is behind what it already read (a restore or a failover that lost writes).
/// </para>
/// </remarks>
internal sealed class PostgreSqlBackplane : IBackplane, IAsyncDisposable, IDisposable
{
    internal const int BatchSize = 1000;
    private const int MaxBatchesPerPoll = 10;
    internal const int CleanupBatchSize = 5000;
    private const int MaxCleanupBatches = 20;

    // Polls a message whose handler keeps failing is retried before it is replaced by a clear, so it can't hold back
    // every later invalidation until the outage clear after half the retention
    internal const int MaxDeliveryAttempts = 5;

    // Polls in a row that found other instances' invalidations no notification announced, before a warning
    internal const int SilentPollsBeforeWarning = 3;

    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstPollMargin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FirstListenRetry = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxListenRetry = TimeSpan.FromSeconds(30);

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlConnection _connection;
    private readonly PostgreSqlOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly byte[] _scope;
    private readonly string _publishSql;
    private readonly string _startSql;
    private readonly string _pollSql;
    private readonly string _retentionSql;
    private readonly string _cleanupSql;
    private int _connectionState = (int)BackplaneState.Connecting;
    private bool _listeningOpen;
    private int _rejectedWarning;
    private CancellationTokenSource? _stopping;
    private Task? _polling;
    private Task? _listening;
    private TaskCompletionSource _doorbell = NewDoorbell();
    private int _notified;
    private int _silentPolls;
    private bool _silentWarning;
    private long? _lastId;
    private PendingBatch? _pendingBatch;
    private DateTime _lastPolled;
    private DateTime _nextCleanup;
    private bool _failing;
    private long _subscribedAt;

    public PostgreSqlBackplane(PostgreSqlConnection connection, PostgreSqlOptions options, ILogger<PostgreSqlBackplane>? logger = null,
        TimeProvider? timeProvider = null, string prefix = "methodcache:")
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _dataSource = connection.DataSource;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _subscribedAt = _timeProvider.GetTimestamp();
        _scope = GetScope(prefix);
        Channel = GetChannel(options, _scope);

        var invalidations = options.QualifiedInvalidationsTableName;
        var cursor = options.QualifiedCursorTableName;
        const string clock = "(extract(epoch FROM clock_timestamp()) * 1000000)::bigint";
        // The cursor row stays locked until commit, so the next invalidation of this prefix waits for this one to be
        // visible. pg_notify is sent at commit, after the row is readable. created_at is read after the wait for the
        // cursor row, not at the start of the statement as now() would be, since a new instance's first poll compares
        // it with the time it subscribed.
        // Messages commit without waiting for the disk, like values: the stamp the invalidation wrote first is durable,
        // so a message lost in a server crash only leaves other instances' in-memory copies, as a lost Redis pub/sub
        // message would; instances that already read it see the cursor go back and clear their memory.
        _publishSql = $"""
            WITH durability AS (SELECT set_config('synchronous_commit', 'off', true)),
            next AS (
                INSERT INTO {cursor} AS c (scope, last_id) SELECT $1, {clock} FROM durability
                ON CONFLICT (scope) DO UPDATE SET last_id = GREATEST(c.last_id + 1, {clock})
                RETURNING last_id),
            added AS (
                INSERT INTO {invalidations} (scope, id, instance_id, message_type, target, created_at)
                SELECT $1, last_id, $2, $3, $4, clock_timestamp() FROM next
                RETURNING id)
            SELECT pg_notify($5, $2) FROM added
            """;
        // Where a new instance starts reading: after the invalidations written well before it subscribed
        _startSql = $"""
            SELECT coalesce(
                (SELECT max(id) FROM {invalidations} WHERE scope = $1 AND created_at < now() - $2),
                (SELECT min(id) - 1 FROM {invalidations} WHERE scope = $1),
                0)
            """;
        // One statement, one snapshot: the cursor and the rows after the last id read
        _pollSql = $"""
            SELECT c.last_id, i.id, i.instance_id, i.message_type, i.target
            FROM {cursor} c
            LEFT JOIN LATERAL (
                SELECT id, instance_id, message_type, target FROM {invalidations}
                WHERE scope = c.scope AND id > $2 ORDER BY id LIMIT {BatchSize}) i ON true
            WHERE c.scope = $1
            ORDER BY i.id
            """;
        // The cursor row keeps the longest retention of the prefix's instances, so none of them deletes invalidations
        // another one still counts on: that one clears its memory only after half of its own retention. Only a missing
        // row or a shorter retention takes the cursor row's lock.
        _retentionSql = $"""
            INSERT INTO {cursor} AS c (scope, last_id, retention)
            SELECT $1, 0, $2 WHERE NOT EXISTS (SELECT FROM {cursor} WHERE scope = $1 AND retention >= $2)
            ON CONFLICT (scope) DO UPDATE SET retention = GREATEST(c.retention, EXCLUDED.retention)
            """;
        // Instances sharing a prefix share the work; other prefixes may keep their rows longer
        _cleanupSql = $"""
            DELETE FROM {invalidations} WHERE scope = $1 AND id IN (
                SELECT id FROM {invalidations}
                WHERE scope = $1 AND created_at < now() - GREATEST($2, (SELECT retention FROM {cursor} WHERE scope = $1))
                LIMIT {CleanupBatchSize} FOR UPDATE SKIP LOCKED)
            """;
    }

    /// <inheritdoc />
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public BackplaneState ConnectionState => (BackplaneState)Volatile.Read(ref _connectionState);

    /// <summary>The notification channel of this prefix and invalidations table.</summary>
    internal string Channel { get; }

    /// <summary>The statement that writes an invalidation; tests run it inside their own transactions.</summary>
    internal string PublishSql => _publishSql;

    /// <summary>Whether the <c>LISTEN</c> connection is open.</summary>
    internal bool Listening
    {
        get => Volatile.Read(ref _listeningOpen);
        private set => Volatile.Write(ref _listeningOpen, value);
    }

    /// <inheritdoc />
    public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default)
        => PublishAsync(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, InstanceId = InstanceId, Key = key }, cancellationToken);

    /// <inheritdoc />
    public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
        => PublishAsync(new BackplaneMessage { Type = BackplaneMessageType.TagInvalidation, InstanceId = InstanceId, Tag = tag }, cancellationToken);

    /// <inheritdoc />
    public Task PublishMethodInvalidationAsync(string methodId, CancellationToken cancellationToken = default)
        => PublishAsync(new BackplaneMessage { Type = BackplaneMessageType.MethodInvalidation, InstanceId = InstanceId, Key = methodId }, cancellationToken);

    /// <inheritdoc />
    public Task PublishClearAsync(CancellationToken cancellationToken = default)
        => PublishAsync(new BackplaneMessage { Type = BackplaneMessageType.ClearAll, InstanceId = InstanceId }, cancellationToken);

    /// <inheritdoc />
    public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        if (_stopping != null) throw new InvalidOperationException("The backplane already has a subscriber.");

        Volatile.Write(ref _connectionState, (int)BackplaneState.Connecting);
        _subscribedAt = _timeProvider.GetTimestamp();
        _stopping = new CancellationTokenSource();
        var stopping = _stopping.Token;
        _polling = Task.Run(() => PollAsync(onMessage, stopping), CancellationToken.None);
        _listening = Task.Run(() => ListenAsync(stopping), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _connectionState, (int)BackplaneState.Disconnected);
        var stopping = Interlocked.Exchange(ref _stopping, null);
        if (stopping == null) return;

        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            foreach (var task in new[] { _polling, _listening })
            {
                if (task == null) continue;
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            stopping.Dispose();
            _polling = null;
            _listening = null;
        }
    }

    public ValueTask DisposeAsync() => new(UnsubscribeAsync());

    public void Dispose()
    {
        // Stops without waiting for a poll in progress to finish
        Volatile.Write(ref _connectionState, (int)BackplaneState.Disconnected);
        var stopping = Interlocked.Exchange(ref _stopping, null);
        if (stopping == null) return;
        stopping.Cancel();
        stopping.Dispose();
    }

    private async Task PublishAsync(BackplaneMessage message, CancellationToken cancellationToken)
    {
        var wire = BackplaneWire.Encode(message);
        using var command = _dataSource.CreateCommand(_publishSql);
        command.Parameters.Add(PostgreSqlDistributedCache.Bytea(_scope));
        command.Parameters.Add(Text(InstanceId));
        command.Parameters.Add(new NpgsqlParameter<short> { NpgsqlDbType = NpgsqlDbType.Smallint, TypedValue = BackplaneWire.Version });
        command.Parameters.Add(Text(wire));
        command.Parameters.Add(Text(Channel));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polls at once, then every interval, and at once when a notification rings the doorbell.</summary>
    private async Task PollAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.BackplanePollingInterval, _timeProvider);
        Task<bool>? tick = null;
        // The first poll finds where to start before any notification arrives, so the first one already delivers
        var due = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            tick ??= timer.WaitForNextTickAsync(cancellationToken).AsTask();
            var doorbell = Volatile.Read(ref _doorbell).Task;
            if (!due && await Task.WhenAny(tick, doorbell).ConfigureAwait(false) == tick)
            {
                if (!await tick.ConfigureAwait(false)) return;
                tick = null;
            }

            due = false;

            // A notification after this point rings the new doorbell, so it is never lost between two polls
            Interlocked.Exchange(ref _doorbell, NewDoorbell());
            var notified = Interlocked.Exchange(ref _notified, 0) != 0;
            try
            {
                var foreign = await PollOnceAsync(onMessage, cancellationToken).ConfigureAwait(false);
                CheckNotifications(foreign, notified);
                if (_failing)
                {
                    _failing = false;
                    _logger.LogInformation("The MethodCache backplane reads invalidations from PostgreSQL again.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The rows stay in the table, so the next poll picks up whatever this one missed
                if (!_failing)
                {
                    _failing = true;
                    _logger.LogWarning("The MethodCache backplane could not read invalidations from PostgreSQL; retrying every {Interval}. Error type: {ErrorType}.",
                        _options.BackplanePollingInterval, ex.GetType().FullName);
                }
            }
        }
    }

    /// <summary>
    /// Warns once when <c>LISTEN</c> seems connected but other instances' invalidations keep arriving without a
    /// notification, as behind a pooler in transaction mode: they then arrive with polling only.
    /// </summary>
    private void CheckNotifications(bool foreign, bool notified)
    {
        if (!foreign || notified || !Listening)
        {
            if (notified) _silentPolls = 0;
            return;
        }

        if (++_silentPolls >= SilentPollsBeforeWarning && !_silentWarning)
        {
            _silentWarning = true;
            _logger.LogWarning("MethodCache receives no PostgreSQL notifications for invalidations other instances write, so they arrive within {Interval} by polling. LISTEN needs a direct connection or a pooler in session mode.",
                _options.BackplanePollingInterval);
        }
    }

    /// <summary>Reads and delivers new invalidations. Returns whether any came from another instance.</summary>
    internal async Task<bool> PollOnceAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken)
    {
        var answered = true;
        try
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            // Since the last poll, or since subscribing before the first one
            var unread = _lastId == null ? _timeProvider.GetElapsedTime(_subscribedAt) : now - _lastPolled;
            var outage = unread >= _options.BackplaneRetention / 2;
            if (_lastId == null || outage)
            {
                // First poll: start after the invalidations written before this instance subscribed, with a margin, since
                // it may have cached values since then. After a longer outage, before the first poll too, rows this
                // instance never read may already be deleted, so it drops everything it holds in memory and starts from now.
                var lastId = await ReadStartAsync(outage ? TimeSpan.Zero : unread + FirstPollMargin, cancellationToken).ConfigureAwait(false);
                if (outage)
                {
                    _logger.LogWarning("The MethodCache backplane could not read invalidations for {Outage}; it clears this instance's in-memory cache.", unread);
                    await onMessage(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }).ConfigureAwait(false);
                }

                // A failed recovery clear must be attempted again; none of its state is acknowledged beforehand
                _lastId = lastId;
                _pendingBatch = null;
                _lastPolled = now;
                // Then read what was written since, so the poll a notification triggered delivers it
            }

            var foreign = false;
            for (var batch = 0; batch < MaxBatchesPerPoll; batch++)
            {
                var pending = _pendingBatch ??= await ReadAsync(cancellationToken).ConfigureAwait(false);
                if (pending.Behind)
                {
                    // The database lost invalidations this instance already read, so it may also have lost ones it hasn't
                    _logger.LogWarning("The MethodCache invalidation cursor in PostgreSQL went back (restore or failover); clearing this instance's in-memory cache.");
                    await onMessage(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }).ConfigureAwait(false);
                    _lastId = pending.LastId;
                    _pendingBatch = null;
                    break;
                }

                foreign |= pending.Messages.Count > 0;
                while (pending.DeliveredCount < pending.Messages.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DeliverAsync(onMessage, pending, cancellationToken).ConfigureAwait(false);
                    pending.DeliveredCount++;
                    pending.FailedAttempts = 0;
                }

                _lastId = pending.LastId;
                _pendingBatch = null;
                if (pending.Rows < BatchSize) break;
            }

            _lastPolled = now;
            await CleanupAsync(now, cancellationToken).ConfigureAwait(false);
            return foreign;
        }
        catch { answered = false; throw; }
        finally
        {
            Volatile.Write(ref _connectionState, (int)(answered && !cancellationToken.IsCancellationRequested
                ? BackplaneState.Connected : BackplaneState.Disconnected));
        }
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
                _logger.LogWarning("The MethodCache backplane could not apply a {Type} invalidation after {Attempts} attempts; it clears this instance's in-memory cache instead. Error type: {ErrorType}.",
                    message.Type, MaxDeliveryAttempts, ex.GetType().FullName);
            }
        }

        await onMessage(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }).ConfigureAwait(false);
    }

    private async Task<long> ReadStartAsync(TimeSpan age, CancellationToken cancellationToken)
    {
        using var command = _dataSource.CreateCommand(_startSql);
        command.Parameters.Add(PostgreSqlDistributedCache.Bytea(_scope));
        command.Parameters.Add(new NpgsqlParameter<TimeSpan> { NpgsqlDbType = NpgsqlDbType.Interval, TypedValue = age });
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>Reads the invalidations after the last id read, and checks the cursor has not gone back.</summary>
    private async Task<PendingBatch> ReadAsync(CancellationToken cancellationToken)
    {
        var lastId = _lastId!.Value;
        var messages = new List<BackplaneMessage>();
        var rows = 0;
        long? cursor = null;
        using var command = _dataSource.CreateCommand(_pollSql);
        command.Parameters.Add(PostgreSqlDistributedCache.Bytea(_scope));
        command.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint, TypedValue = lastId });
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cursor = reader.GetInt64(0);
                if (reader.IsDBNull(1)) break;
                rows++;
                lastId = reader.GetInt64(1);
                AddMessage(reader.GetString(2), reader.GetInt16(3), reader.GetString(4), messages);
            }
        }

        // No cursor row: nothing was ever written for this prefix, or the tables were emptied
        return (cursor ?? 0) < _lastId.Value
            ? new PendingBatch([], 0, cursor ?? 0, Behind: true)
            : new PendingBatch(messages, rows, lastId, Behind: false);
    }

    private void AddMessage(string instanceId, short type, string wire, List<BackplaneMessage> messages)
    {
        if (instanceId == InstanceId) return;
        var message = CreateWireMessage(type, wire, instanceId);
        if (message == null)
        {
            if (Interlocked.Exchange(ref _rejectedWarning, 1) == 0)
                _logger.LogWarning("Rejected an incompatible or malformed MethodCache backplane frame; clearing this instance's memory.");
            message = new BackplaneMessage { Type = BackplaneMessageType.ClearAll };
        }

        messages.Add(message);
    }

    internal static BackplaneMessage? CreateWireMessage(short type, string wire, string instanceId)
        => type == BackplaneWire.Version && BackplaneWire.TryDecode(wire, out var message) && message.InstanceId == instanceId ? message : null;

    private async Task CleanupAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (now < _nextCleanup) return;
        _nextCleanup = now + CleanupInterval;

        using (var register = _dataSource.CreateCommand(_retentionSql))
        {
            register.Parameters.Add(PostgreSqlDistributedCache.Bytea(_scope));
            register.Parameters.Add(new NpgsqlParameter<TimeSpan> { NpgsqlDbType = NpgsqlDbType.Interval, TypedValue = _options.BackplaneRetention });
            await register.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Small batches keep locks short; repeat until the table is caught up, however fast rows are written
        for (var batch = 0; batch < MaxCleanupBatches; batch++)
        {
            using var command = _dataSource.CreateCommand(_cleanupSql);
            command.Parameters.Add(PostgreSqlDistributedCache.Bytea(_scope));
            command.Parameters.Add(new NpgsqlParameter<TimeSpan> { NpgsqlDbType = NpgsqlDbType.Interval, TypedValue = _options.BackplaneRetention });
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) < CleanupBatchSize) return;
        }
    }

    /// <summary>
    /// Keeps one connection listening on <see cref="Channel"/>, reconnecting with backoff. Notifications from
    /// this instance are ignored; every other one rings the poll's doorbell.
    /// </summary>
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        var retry = FirstListenRetry;
        var failing = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var connection = new NpgsqlConnection(_connection.ListenConnectionString);
                await using (connection.ConfigureAwait(false))
                {
                    connection.Notification += OnNotification;
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    using (var listen = new NpgsqlCommand("LISTEN " + PostgreSqlOptions.Quote(Channel), connection))
                        await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    Listening = true;
                    // Invalidations written while this instance was not listening rang no doorbell: read them now
                    Volatile.Read(ref _doorbell).TrySetResult();
                    retry = FirstListenRetry;
                    if (failing)
                    {
                        failing = false;
                        _logger.LogInformation("The MethodCache backplane receives PostgreSQL notifications again.");
                    }

                    while (true) await connection.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Polling still delivers every invalidation; only the shortcut is gone until the connection is back
                if (!failing)
                {
                    failing = true;
                    _logger.LogWarning("The MethodCache backplane lost its PostgreSQL LISTEN connection; invalidations arrive by polling every {Interval} until it reconnects. Error type: {ErrorType}.",
                        _options.BackplanePollingInterval, ex.GetType().FullName);
                }
            }
            finally
            {
                Listening = false;
            }

            try
            {
                await Task.Delay(retry, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxListenRetry.Ticks));
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        if (e.Payload == InstanceId) return;
        Volatile.Write(ref _notified, 1);
        Volatile.Read(ref _doorbell).TrySetResult();
    }

    private static TaskCompletionSource NewDoorbell() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static NpgsqlParameter<string> Text(string value) => new() { NpgsqlDbType = NpgsqlDbType.Text, TypedValue = value };

    /// <summary>The rows of one prefix: the SHA-256 of its exact UTF-16 code units.</summary>
    internal static byte[] GetScope(string prefix) => SHA256.HashData(LosslessUtf16.GetBytes(prefix));

    /// <summary>
    /// A channel per prefix and invalidations table, so instances are not woken by tables or prefixes they don't
    /// read. 44 lowercase characters, within PostgreSQL's 63-byte identifiers.
    /// </summary>
    internal static string GetChannel(PostgreSqlOptions options, byte[] scope)
    {
        var table = Encoding.UTF8.GetBytes(options.QualifiedInvalidationsTableName);
        var hash = SHA256.HashData([.. scope, .. table]);
        return "methodcache_" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private sealed record PendingBatch(List<BackplaneMessage> Messages, int Rows, long LastId, bool Behind)
    {
        public int DeliveredCount { get; set; }

        /// <summary>Polls in which the message at <see cref="DeliveredCount"/> failed.</summary>
        public int FailedAttempts { get; set; }
    }
}
