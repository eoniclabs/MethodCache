using System.Collections.Concurrent;
using StackExchange.Redis;

namespace MethodCache.Redis.IntegrationTests;

// Redis publishes a keyspace event for every write command, including an HSET that stores identical values,
// so a test can prove that an operation wrote nothing without reading server-wide statistics.
internal sealed class KeyWrites : IAsyncDisposable
{
    private const string KeyspacePrefix = "__keyspace@0__:";
    private readonly ConnectionMultiplexer _admin;
    private readonly IServer _server;
    private readonly string _previousEvents;
    private readonly HashSet<string> _keys;
    private readonly string _marker = "key-writes-marker:" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentQueue<string> _events = new();
    private ChannelMessageQueue? _queue;
    private TaskCompletionSource _markerSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private KeyWrites(ConnectionMultiplexer admin, string previousEvents, IEnumerable<RedisKey> keys)
    {
        _admin = admin;
        _server = admin.GetServer(admin.GetEndPoints()[0]);
        _previousEvents = previousEvents;
        _keys = keys.Select(key => (string)key!).ToHashSet(StringComparer.Ordinal);
    }

    internal static async Task<KeyWrites> WatchAsync(string configuration, IEnumerable<RedisKey> keys)
    {
        var admin = await ConnectionMultiplexer.ConnectAsync(configuration + ",allowAdmin=true");
        var server = admin.GetServer(admin.GetEndPoints()[0]);
        var previous = (await server.ConfigGetAsync("notify-keyspace-events")).SingleOrDefault().Value ?? "";
        var watch = new KeyWrites(admin, previous, keys);
        // K: keyspace channel per key; g: generic commands such as PERSIST and EXPIRE; h: hash commands; $: SET (the marker)
        await server.ConfigSetAsync("notify-keyspace-events", "Kgh$");
        // One subscription read as a queue: StackExchange.Redis runs callback handlers on the thread pool, where the
        // marker's event can overtake earlier ones, but hands a queue's messages to OnMessage one at a time, in order
        watch._queue = await admin.GetSubscriber().SubscribeAsync(RedisChannel.Pattern(KeyspacePrefix + "*"));
        watch._queue.OnMessage(message =>
        {
            var key = ((string)message.Channel!)[KeyspacePrefix.Length..];
            if (key == watch._marker) watch._markerSeen.TrySetResult();
            else if (watch._keys.Contains(key)) watch._events.Enqueue(message.Message!);
        });
        return watch;
    }

    // The server publishes events in command order on one subscriber connection, and the queue keeps that order,
    // so once the marker's event arrives, every write event from earlier commands has arrived too.
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
        if (_queue is not null) await _queue.UnsubscribeAsync();
        await _admin.GetDatabase().KeyDeleteAsync(_marker);
        await _server.ConfigSetAsync("notify-keyspace-events", _previousEvents);
        await _admin.DisposeAsync();
    }
}
