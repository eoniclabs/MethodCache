using StackExchange.Redis;

namespace MethodCache.Redis;

/// <summary>
/// The Redis connection MethodCache uses, opened on first use so an unreachable Redis never delays startup.
/// </summary>
internal sealed class RedisConnection : IDisposable
{
    private readonly Func<Task<IConnectionMultiplexer>> _connect;
    private readonly object _lock = new();
    private readonly bool _owned;
    private Task<IConnectionMultiplexer>? _connection;

    public RedisConnection(RedisOptions options, IServiceProvider services)
        : this(Connect(options, services), owned: options.ConnectionMultiplexerFactory == null)
    {
    }

    internal RedisConnection(Func<Task<IConnectionMultiplexer>> connect, bool owned)
    {
        _connect = connect;
        _owned = owned;
    }

    private static Func<Task<IConnectionMultiplexer>> Connect(RedisOptions options, IServiceProvider services)
    {
        // The app's own connection is used as is, and left to the app to dispose
        if (options.ConnectionMultiplexerFactory is { } factory)
        {
            return () => Task.FromResult(factory(services));
        }

        // Options validation guarantees a configuration string whenever there is no factory
        var configuration = options.Configuration ?? throw new InvalidOperationException(
            Microsoft.Extensions.DependencyInjection.RedisMethodCacheBuilderExtensions.MissingConnectionMessage);
        return () => ConnectAsync(configuration);
    }

    /// <summary>
    /// The connection, opened by the first call. Callers that arrive while it is opening share that one attempt. A
    /// connect that failed (the app's factory threw while Redis was down, say) is tried again by the next call instead
    /// of failing every later one.
    /// </summary>
    /// <remarks>
    /// The connect runs on the thread pool, outside the lock: the app's factory may block (ConnectionMultiplexer.Connect
    /// does, for as long as Redis takes to answer), and neither the caller, which then serves from memory, nor any
    /// other caller waits for it.
    /// </remarks>
    public Task<IConnectionMultiplexer> GetAsync()
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is { IsFaulted: false, IsCanceled: false })
        {
            return connection;
        }

        lock (_lock)
        {
            if (_connection is null or { IsFaulted: true } or { IsCanceled: true })
            {
                // Only starts the attempt; an exception the factory throws faults the task
                _connection = Task.Run(_connect);
            }

            return _connection;
        }
    }

    public void Dispose()
    {
        Task<IConnectionMultiplexer>? connection;
        lock (_lock) connection = _connection;
        if (!_owned || connection == null)
        {
            return;
        }

        // A connection still opening is closed once it is open, so it doesn't outlive its owner
        _ = connection.ContinueWith(
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
