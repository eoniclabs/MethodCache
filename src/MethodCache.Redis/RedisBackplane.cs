using MethodCache.Extensibility;
using StackExchange.Redis;

namespace MethodCache.Redis;

/// <summary>
/// Sends cache invalidations between instances over Redis pub/sub.
/// </summary>
/// <remarks>
/// Pub/sub delivers nothing while a connection is down, so when the connection comes back this instance treats it
/// as a clear and drops its in-memory entries; they are read again from Redis.
/// </remarks>
internal sealed class RedisBackplane : IBackplane, IDisposable, IAsyncDisposable
{
    private const char Separator = '\n';

    private readonly Func<Task<IConnectionMultiplexer>> _connect;
    private readonly RedisChannel _channel;
    private readonly object _lifecycleLock = new();
    private Subscription? _subscription;
    private Task? _shutdown;
    private Task _cleanupTask = Task.CompletedTask;
    private bool _disposed;

    public RedisBackplane(Func<Task<IConnectionMultiplexer>> connect, string channel)
    {
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        ArgumentException.ThrowIfNullOrEmpty(channel);
        _channel = RedisChannel.Literal(channel);
    }

    /// <inheritdoc />
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.KeyInvalidation, key);

    /// <inheritdoc />
    public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
        => PublishAsync(BackplaneMessageType.TagInvalidation, tag);

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

    private async Task StartSubscriptionAsync(Subscription subscription)
    {
        try
        {
            var connection = await _connect().WaitAsync(subscription.Cancellation.Token).ConfigureAwait(false);
            subscription.Cancellation.Token.ThrowIfCancellationRequested();
            subscription.Connection = connection;
            subscription.Handler = (_, value) =>
            {
                if (!subscription.Cancellation.IsCancellationRequested &&
                    Volatile.Read(ref subscription.Callback) is { } callback && TryParse(value, out var message))
                    Observe(callback(message));
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
                // The multiplexer retains the registration and subscribes again when it reconnects.
            }
            subscription.Completion.TrySetResult();
        }
        catch (OperationCanceledException) when (subscription.Cancellation.IsCancellationRequested)
        {
            subscription.Completion.TrySetCanceled(subscription.Cancellation.Token);
        }
        catch (Exception ex)
        {
            subscription.Completion.TrySetException(ex);
        }
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

    private async Task PublishAsync(BackplaneMessageType type, string value)
    {
        var connection = await _connect().ConfigureAwait(false);
        await connection.GetSubscriber().PublishAsync(_channel, Format(type, InstanceId, value)).ConfigureAwait(false);
    }

    internal static string Format(BackplaneMessageType type, string instanceId, string value)
    {
        // Type, sender and value on separate lines; the value is last, so it may contain anything
        return string.Concat(((int)type).ToString(System.Globalization.CultureInfo.InvariantCulture), "\n", instanceId, "\n", MethodCache.Infrastructure.LosslessWireText.Encode(value));
    }

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
    {
        message = null!;
        var text = (string?)value;
        if (text == null)
        {
            return false;
        }

        var first = text.IndexOf(Separator);
        var second = first < 0 ? -1 : text.IndexOf(Separator, first + 1);
        if (second < 0 || !int.TryParse(text.AsSpan(0, first), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var type))
        {
            return false;
        }

        var instanceId = text.Substring(first + 1, second - first - 1);
        if (!MethodCache.Infrastructure.LosslessWireText.TryDecode(text.Substring(second + 1), out var target)) return false;
        message = (BackplaneMessageType)type switch
        {
            BackplaneMessageType.KeyInvalidation => new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = target, InstanceId = instanceId },
            BackplaneMessageType.TagInvalidation => new BackplaneMessage { Type = BackplaneMessageType.TagInvalidation, Tag = target, InstanceId = instanceId },
            _ => null!
        };

        return message != null;
    }
}
