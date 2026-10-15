using MethodCache.Extensibility;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MethodCache.Redis;

/// <summary>
/// Sends cache invalidations between instances over Redis pub/sub.
/// </summary>
/// <remarks>
/// Pub/sub delivers nothing while a connection is down, so when the connection comes back this instance treats it
/// as a clear and drops its in-memory entries; they are read again from Redis. A subscription that can't connect is
/// retried, waiting longer each time up to <see cref="MaxRetryDelay"/>, until it succeeds or is cancelled.
/// </remarks>
internal sealed class RedisBackplane : IBackplane, IDisposable, IAsyncDisposable
{

    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly Func<Task<IConnectionMultiplexer>> _connect;
    private readonly RedisChannel _channel;
    private readonly object _lifecycleLock = new();
    private Subscription? _subscription;
    private Task? _shutdown;
    private Task _cleanupTask = Task.CompletedTask;
    private bool _disposed;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private int _rejectedWarning;

    public RedisBackplane(Func<Task<IConnectionMultiplexer>> connect, string channel, ILogger<RedisBackplane>? logger = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        ArgumentException.ThrowIfNullOrEmpty(channel);
        _channel = RedisChannel.Literal(channel);
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
    public Task PublishMethodInvalidationAsync(string methodId, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.MethodInvalidation, methodId, cancellationToken);


    /// <inheritdoc />
    public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_subscription != null) throw new InvalidOperationException("The backplane already has a subscriber.");
            var subscription = new Subscription(onMessage, cancellationToken);
            _subscription = subscription;
            _ = StartSubscriptionAsync(subscription);
            return subscription.Completion.Task;
        }
    }

    public Task PublishClearAsync(CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.ClearAll, string.Empty, cancellationToken);

    private async Task StartSubscriptionAsync(Subscription subscription)
    {
        // Until Redis answers (it may be down when the app starts), try again, waiting longer each time
        var delay = FirstRetryDelay;
        var failed = false;
        try
        {
            while (true)
            {
                try
                {
                    var connection = await _connect().WaitAsync(subscription.Cancellation.Token).ConfigureAwait(false);
                    if (await TrySubscribeAsync(subscription, connection, failed).ConfigureAwait(false))
                    {
                        subscription.Completion.TrySetResult();
                        return;
                    }
                }
                catch (OperationCanceledException) when (subscription.Cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MethodCache could not connect its Redis backplane; it tries again in {Delay}. Until then other instances' invalidations don't reach this instance's memory.", delay);
                }

                failed = true;
                await Task.Delay(delay, _time, subscription.Cancellation.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (subscription.Cancellation.IsCancellationRequested)
        {
            subscription.Completion.TrySetCanceled(subscription.Cancellation.Token);
        }
    }

    /// <summary>
    /// Attaches the subscription to the channel; false, with a warning and nothing left registered, when Redis
    /// refused the subscribe (a timeout, say), so it is tried again.
    /// </summary>
    /// <param name="subscription">The subscription to attach to the channel.</param>
    /// <param name="connection">The connection to subscribe on.</param>
    /// <param name="missed">Whether earlier attempts failed, so invalidations sent meanwhile never arrived.</param>
    private async Task<bool> TrySubscribeAsync(Subscription subscription, IConnectionMultiplexer connection, bool missed)
    {
        subscription.Cancellation.Token.ThrowIfCancellationRequested();
        subscription.Connection = connection;
        subscription.Handler = (_, value) =>
        {
            if (!subscription.Cancellation.IsCancellationRequested &&
                Volatile.Read(ref subscription.Callback) is { } callback)
            {
                if (TryParse(value, out var message)) Observe(callback(message));
                else
                {
                    if (Interlocked.Exchange(ref _rejectedWarning, 1) == 0)
                        _logger.LogWarning("Rejected an incompatible or malformed MethodCache backplane frame; clearing this instance's memory.");
                    Observe(callback(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }));
                }
            }
        };
        subscription.Restored = (_, _) =>
        {
            if (!subscription.Cancellation.IsCancellationRequested && Volatile.Read(ref subscription.Callback) is { } callback)
                Observe(callback(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }));
        };
        connection.ConnectionRestored += subscription.Restored;
        try
        {
            await connection.GetSubscriber().SubscribeAsync(_channel, subscription.Handler).ConfigureAwait(false);
        }
        catch (RedisConnectionException)
        {
            // The multiplexer retains the registration and subscribes again when it reconnects, and then
            // Restored clears.
            missed = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MethodCache could not subscribe its Redis backplane; it tries again. Until then other instances' invalidations don't reach this instance's memory.");
            connection.ConnectionRestored -= subscription.Restored;
            try { await connection.GetSubscriber().UnsubscribeAsync(_channel, subscription.Handler).ConfigureAwait(false); }
            catch (Exception) { /* The handler is removed locally before UNSUBSCRIBE is sent. */ }
            subscription.Connection = null;
            subscription.Handler = null;
            return false;
        }

        // Invalidations other instances sent while this one couldn't subscribe never arrived, so what it
        // cached meanwhile may be stale, as after a reconnect
        if (missed && !subscription.Cancellation.IsCancellationRequested && Volatile.Read(ref subscription.Callback) is { } clear)
        {
            Observe(clear(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }));
        }

        return true;
    }

    /// <inheritdoc />
    public Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        Task cleanup;
        lock (_lifecycleLock)
        {
            var subscription = _subscription;
            _subscription = null;
            if (subscription == null) return _cleanupTask.WaitAsync(cancellationToken);
            Volatile.Write(ref subscription.Callback, null);
            subscription.Cancellation.Cancel();
            var removing = CleanupSubscriptionAsync(subscription);
            cleanup = _cleanupTask = _cleanupTask.IsCompletedSuccessfully ? removing : Task.WhenAll(_cleanupTask, removing);
        }
        return cleanup.WaitAsync(cancellationToken);
    }

    private async Task CleanupSubscriptionAsync(Subscription subscription)
    {
        try { await subscription.Completion.Task.ConfigureAwait(false); }
        catch (Exception) { /* Failed or cancelled subscriptions still need their registrations removed. */ }
        try
        {
            if (subscription.Connection is { } connection)
            {
                connection.ConnectionRestored -= subscription.Restored;
                if (subscription.Handler is { } handler)
                {
                    try { await connection.GetSubscriber().UnsubscribeAsync(_channel, handler).ConfigureAwait(false); }
                    catch (RedisConnectionException) { /* The client removes its local handler before sending UNSUBSCRIBE. */ }
                    catch (RedisTimeoutException) { /* A missing server acknowledgement cannot restore the removed handler. */ }
                    catch (ObjectDisposedException) { /* The application may already have closed its borrowed connection. */ }
                    catch (OperationCanceledException) { /* A cancelled acknowledgement cannot restore the removed local handler. */ }
                }
            }
        }
        finally { subscription.Cancellation.Dispose(); }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown = UnsubscribeAsync();
            Observe(_shutdown);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task shutdown;
        lock (_lifecycleLock) shutdown = _shutdown!;
        await shutdown.ConfigureAwait(false);
    }

    private sealed class Subscription(Func<BackplaneMessage, Task> callback, CancellationToken cancellationToken)
    {
        public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<BackplaneMessage, Task>? Callback = callback;
        public IConnectionMultiplexer? Connection;
        public Action<RedisChannel, RedisValue>? Handler;
        public EventHandler<ConnectionFailedEventArgs>? Restored;
    }

    private async Task PublishAsync(BackplaneMessageType type, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await _connect().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await connection.GetSubscriber().PublishAsync(_channel, Format(type, InstanceId, value)).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static string Format(BackplaneMessageType type, string instanceId, string value)
        => MethodCache.Infrastructure.BackplaneWire.Encode(new BackplaneMessage
        {
            Type = type,
            InstanceId = instanceId,
            Key = type is BackplaneMessageType.KeyInvalidation or BackplaneMessageType.MethodInvalidation ? value : null,
            Tag = type == BackplaneMessageType.TagInvalidation ? value : null
        });

    private static void Observe(Task task)
    {
        // The subscriber reports its own errors; this only keeps a failed task from going unobserved
        if (!task.IsCompleted)
        {
            task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    internal static bool TryParse(RedisValue value, out BackplaneMessage message)
        => MethodCache.Infrastructure.BackplaneWire.TryDecode((string?)value, out message);
}
