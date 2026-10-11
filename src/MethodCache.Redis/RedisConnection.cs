using StackExchange.Redis;

namespace MethodCache.Redis;

/// <summary>
/// The Redis connection MethodCache uses, opened on first use so an unreachable Redis never delays startup.
/// </summary>
internal sealed class RedisConnection : IDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;
    private readonly bool _owned;

    public RedisConnection(RedisOptions options, IServiceProvider services)
        : this(Connect(options, services), owned: options.ConnectionMultiplexerFactory == null)
    {
    }

    internal RedisConnection(Func<Task<IConnectionMultiplexer>> connect, bool owned)
    {
        _connection = new Lazy<Task<IConnectionMultiplexer>>(connect);
        _owned = owned;
    }

    private static Func<Task<IConnectionMultiplexer>> Connect(RedisOptions options, IServiceProvider services)
    {
        // The app's own connection is used as is, and left to the app to dispose
        if (options.ConnectionMultiplexerFactory is { } factory)
        {
            return () => Task.FromResult(factory(services));
        }

        var configuration = options.Configuration;
        return () => ConnectAsync(configuration);
    }

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    public void Dispose()
    {
        if (!_owned || !_connection.IsValueCreated)
        {
            return;
        }

        // A connection still opening is closed once it is open, so it doesn't outlive its owner
        _ = _connection.Value.ContinueWith(
            static connecting => connecting.Result.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<IConnectionMultiplexer> ConnectAsync(string configuration)
    {
        var options = ConfigurationOptions.Parse(configuration);
        // Keep retrying in the background instead of failing while Redis is unreachable, and fail commands at once
        // while it is: a cache miss then runs the method instead of waiting for Redis
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        return await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
    }
}
