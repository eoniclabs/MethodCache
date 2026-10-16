using System.Collections.Concurrent;
using StackExchange.Redis;

namespace MethodCache.Redis.IntegrationTests;

// Redis publishes a keyspace event for every write command, including an HSET that stores identical values,
// so a test can prove that an operation wrote nothing without reading server-wide statistics.
internal sealed class KeyWrites : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _admin;
    private readonly IServer _server;
    private readonly string _previousEvents;
    private readonly RedisKey _marker = "key-writes-marker:" + Guid.NewGuid().ToString("N");
    private readonly List<RedisChannel> _channels = [];
    private readonly ConcurrentQueue<string> _events = new();
    private TaskCompletionSource _markerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private KeyWrites(ConnectionMultiplexer admin, string previousEvents)
    {
        _admin = admin;
        _server = admin.GetServer(admin.GetEndPoints()[0]);
        _previousEvents = previousEvents;
    }

    internal static async Task<KeyWrites> WatchAsync(string configuration, IEnumerable<RedisKey> keys)
    {
        var admin = await ConnectionMultiplexer.ConnectAsync(configuration + ",allowAdmin=true");
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var previous = (await server.ConfigGetAsync("notify-keyspace-events")).SingleOrDefault().Value ?? "";
        var watch = new KeyWrites(admin, previous);
        // K: keyspace channel per key; g: generic commands such as PERSIST and EXPIRE; h: hash commands; $: SET (the marker)
        await server.ConfigSetAsync("notify-keyspace-events", "Kgh$");
        var subscriber = admin.GetSubscriber();
        foreach (var key in keys.Append(watch._marker))
        {
            var channel = RedisChannel.Literal("__keyspace@0__:" + (string)key!);
            var isMarker = key == watch._marker;
            await subscriber.SubscribeAsync(channel, (_, message) =>
            {
                if (isMarker) watch._markerSeen.TrySetResult();
                else watch._events.Enqueue(message!);
            });
            watch._channels.Add(channel);
        }
        return watch;
    }

    // The server publishes events in command order on one subscriber connection, so once the marker's
    // event arrives, every write event from earlier commands has arrived too.
    internal async Task<string[]> CollectAsync()
    {
        await _admin.GetDatabase().StringSetAsync(_marker, 1);
        await _markerSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _markerSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        while (_events.TryDequeue(out var item)) events.Add(item);
        return [.. events];
    }

    public async ValueTask DisposeAsync()
    {
        var subscriber = _admin.GetSubscriber();
        foreach (var channel in _channels) await subscriber.UnsubscribeAsync(channel);
        await _admin.GetDatabase().KeyDeleteAsync(_marker);
        await _server.ConfigSetAsync("notify-keyspace-events", _previousEvents);
        await _admin.DisposeAsync();
    }
}
