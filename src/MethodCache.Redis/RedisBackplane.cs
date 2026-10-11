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
internal sealed class RedisBackplane : IBackplane
{
    private const char Separator = '\n';

    private readonly Func<Task<IConnectionMultiplexer>> _connect;
    private readonly RedisChannel _channel;
    private IConnectionMultiplexer? _subscribed;
    private Action<RedisChannel, RedisValue>? _handler;
    private Func<BackplaneMessage, Task>? _onMessage;

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
    public async Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onMessage);
        if (_handler != null)
        {
            throw new InvalidOperationException("The backplane already has a subscriber.");
        }

        _onMessage = onMessage;
        _handler = (_, value) =>
        {
            if (TryParse(value, out var message))
            {
                Observe(onMessage(message));
            }
        };
        var connection = await _connect().ConfigureAwait(false);
        _subscribed = connection;
        connection.ConnectionRestored += OnConnectionRestored;

        try
        {
            await connection.GetSubscriber().SubscribeAsync(_channel, _handler).ConfigureAwait(false);
        }
        catch (RedisConnectionException)
        {
            // Redis is unreachable right now. The subscription stays registered with the connection, which
            // subscribes again when it reconnects.
        }
    }

    /// <inheritdoc />
    public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        _onMessage = null;
        var connection = Interlocked.Exchange(ref _subscribed, null);
        var handler = Interlocked.Exchange(ref _handler, null);
        if (connection != null)
        {
            connection.ConnectionRestored -= OnConnectionRestored;
            if (handler != null)
            {
                await connection.GetSubscriber().UnsubscribeAsync(_channel, handler).ConfigureAwait(false);
            }
        }
    }

    private async Task PublishAsync(BackplaneMessageType type, string value)
    {
        var connection = await _connect().ConfigureAwait(false);
        await connection.GetSubscriber().PublishAsync(_channel, Format(type, InstanceId, value)).ConfigureAwait(false);
    }

    internal static string Format(BackplaneMessageType type, string instanceId, string value)
    {
        // Type, sender and value on separate lines; the value is last, so it may contain anything
        return string.Concat(((int)type).ToString(System.Globalization.CultureInfo.InvariantCulture), "\n", instanceId, "\n", value);
    }

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        // Invalidations sent while this instance was disconnected were lost
        if (_onMessage is { } onMessage)
        {
            Observe(onMessage(new BackplaneMessage { Type = BackplaneMessageType.ClearAll }));
        }
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
        var target = text.Substring(second + 1);
        message = (BackplaneMessageType)type switch
        {
            BackplaneMessageType.KeyInvalidation => new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = target, InstanceId = instanceId },
            BackplaneMessageType.TagInvalidation => new BackplaneMessage { Type = BackplaneMessageType.TagInvalidation, Tag = target, InstanceId = instanceId },
            _ => null!
        };

        return message != null;
    }
}
