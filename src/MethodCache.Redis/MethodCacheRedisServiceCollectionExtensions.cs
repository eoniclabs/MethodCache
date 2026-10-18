using MethodCache.Extensibility;
using MethodCache;
using MethodCache.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers Redis as MethodCache's distributed cache.
/// </summary>
public static class MethodCacheRedisServiceCollectionExtensions
{
    /// <summary>Selects Redis for method results using the stated connection configuration.</summary>
    public static MethodCacheBuilder UseRedis(this MethodCacheBuilder builder, string configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddMethodCacheRedis(configuration);
        return builder;
    }
    /// <summary>Selects Redis with explicit connection and backplane options.</summary>
    public static MethodCacheBuilder UseRedis(this MethodCacheBuilder builder, Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddMethodCacheRedis(configure);
        return builder;
    }
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
    /// MethodCache keeps its own cache and, unless <see cref="RedisOptions.ConnectionMultiplexerFactory"/> is set,
    /// its own connection, so the app's <c>IDistributedCache</c> and Redis settings are left alone. The connection
    /// opens on first use, so startup never waits for Redis.
    /// </remarks>
    public static IServiceCollection AddMethodCacheRedis(this IServiceCollection services, Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        MethodCache.Infrastructure.OwnedServices.AddOptions(services);
        services.AddOptions<RedisOptions>().Configure(configure)
            .Validate(ValidConnectionSettings,
                "RedisOptions requires valid Configuration or ConnectionMultiplexerFactory.")
            .ValidateOnStart();
        services.TryAddSingleton(static sp =>
        {
            var configured = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisOptions>>().Value;
            return new RedisRuntimeOptions(new RedisOptions
            {
                Configuration = configured.Configuration,
                ConnectionMultiplexerFactory = configured.ConnectionMultiplexerFactory,
                EnableBackplane = configured.EnableBackplane
            }, sp.GetRequiredKeyedService<DistributedCacheOptions>(MethodCache.Infrastructure.OwnedServices.Key).KeyPrefix);
        });
        services.TryAddSingleton(sp => new RedisConnection(sp.GetRequiredService<RedisRuntimeOptions>().Options, sp));
        services.TryAddKeyedSingleton<IBackplane>(MethodCache.Infrastructure.OwnedServices.Key, static (sp, _) =>
        {
            var options = sp.GetRequiredService<RedisRuntimeOptions>();
            return options.Options.EnableBackplane
                ? new RedisBackplane(sp.GetRequiredService<RedisConnection>().GetAsync, options.Prefix + "invalidations",
                    sp.GetService<ILogger<RedisBackplane>>(), sp.GetService<TimeProvider>()) : null!;
        });
        return services.AddMethodCacheDistributedCache(static sp => new RedisDistributedCache(sp.GetRequiredService<RedisConnection>()));
    }

    private static bool ValidConnectionSettings(RedisOptions options)
    {
        if (options.ConnectionMultiplexerFactory != null) return true;
        if (string.IsNullOrWhiteSpace(options.Configuration)) return false;
        try { _ = StackExchange.Redis.ConfigurationOptions.Parse(options.Configuration); return true; }
        catch (ArgumentException) { return false; }
    }
}

internal sealed record RedisRuntimeOptions(RedisOptions Options, string Prefix);
