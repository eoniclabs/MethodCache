using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace MethodCache.Providers.Redis;

public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Uses Redis as MethodCache's distributed cache: results are shared between instances, and invalidations
    /// reach every instance's in-memory cache over Redis pub/sub. Call <c>AddMethodCache()</c> as well.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The StackExchange.Redis configuration string, for example <c>localhost:6379</c></param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddMethodCacheRedis(this IServiceCollection services, string configuration)
    {
        ArgumentException.ThrowIfNullOrEmpty(configuration);
        return services.AddMethodCacheRedis(options => options.Configuration = configuration);
    }

    /// <summary>
    /// Uses Redis as MethodCache's distributed cache: results are shared between instances, and invalidations
    /// reach every instance's in-memory cache over Redis pub/sub. Call <c>AddMethodCache()</c> as well.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configures the Redis connection and backplane</param>
    /// <returns>The service collection for chaining</returns>
    /// <remarks>
    /// An <see cref="IConnectionMultiplexer"/> already registered is reused for both the cache and the backplane.
    /// </remarks>
    public static IServiceCollection AddMethodCacheRedis(this IServiceCollection services, Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RedisOptions();
        configure(options);

        services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var configuration = ConfigurationOptions.Parse(options.Configuration);
            // Keep retrying in the background instead of failing startup while Redis is unreachable, and fail
            // commands at once while it is: a cache miss then runs the method instead of waiting for Redis
            configuration.AbortOnConnectFail = false;
            configuration.BacklogPolicy = BacklogPolicy.FailFast;
            return ConnectionMultiplexer.Connect(configuration);
        });

        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<RedisCacheOptions>()
            .Configure<IServiceProvider>((cacheOptions, provider) =>
                cacheOptions.ConnectionMultiplexerFactory = () => Task.FromResult(provider.GetRequiredService<IConnectionMultiplexer>()));

        if (options.EnableBackplane)
        {
            services.TryAddSingleton<IBackplane>(provider =>
                new RedisBackplane(provider.GetRequiredService<IConnectionMultiplexer>(), options.BackplaneChannel));
        }

        return services.AddMethodCacheDistributedCache(cacheOptions => cacheOptions.KeyPrefix = options.KeyPrefix);
    }
}
