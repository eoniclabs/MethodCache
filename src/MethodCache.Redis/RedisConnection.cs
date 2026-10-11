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
    {
        if (options.ConnectionMultiplexerFactory is { } factory)
        {
            // The app's own connection: used as is, and left to the app to dispose
            _connection = new Lazy<Task<IConnectionMultiplexer>>(() => Task.FromResult(factory(services)));
        }
        else
        {
            _owned = true;
            var configuration = options.Configuration;
            _connection = new Lazy<Task<IConnectionMultiplexer>>(() => ConnectAsync(configuration));
        }
    }

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    public void Dispose()
    {
        if (_owned && _connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
        {
            _connection.Value.Result.Dispose();
        }
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
