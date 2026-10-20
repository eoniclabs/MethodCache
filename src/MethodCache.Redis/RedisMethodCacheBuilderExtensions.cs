using MethodCache.Extensibility;
using MethodCache;
using MethodCache.Redis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers Redis as MethodCache's distributed cache.
/// </summary>
public static class RedisMethodCacheBuilderExtensions
{
    /// <summary>
    /// Uses Redis as MethodCache's distributed cache: results are shared between instances, and invalidations
    /// reach every instance's in-memory cache over Redis pub/sub.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddMethodCache(mc =&gt; ...)</c>.</param>
    /// <param name="configuration">The StackExchange.Redis configuration string, for example <c>localhost:6379</c>.</param>
    /// <returns>The builder, for chaining.</returns>
    public static MethodCacheBuilder UseRedis(this MethodCacheBuilder builder, string configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddRedis(builder.Services, configuration);
        return builder;
    }
    /// <summary>
    /// Uses Redis as MethodCache's distributed cache, with explicit connection and backplane options.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddMethodCache(mc =&gt; ...)</c>.</param>
    /// <param name="configure">Configures the Redis connection and backplane.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// MethodCache keeps its own cache and, unless <see cref="RedisOptions.ConnectionMultiplexerFactory"/> is set,
    /// its own connection, so the app's <c>IDistributedCache</c> and Redis settings are left alone. The connection
    /// opens on first use, so startup never waits for Redis.
    /// </remarks>
    public static MethodCacheBuilder UseRedis(this MethodCacheBuilder builder, Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddRedis(builder.Services, configure);
        return builder;
    }
    private static IServiceCollection AddRedis(IServiceCollection services, string configuration)
    {
        ArgumentException.ThrowIfNullOrEmpty(configuration);
        return AddRedis(services, options => options.Configuration = configuration);
    }

    private static IServiceCollection AddRedis(IServiceCollection services, Action<RedisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        MethodCache.Infrastructure.OwnedServices.AddOptions(services);
        services.AddOptions<RedisOptions>().Configure(configure)
            .Validate(static options => options.ConnectionMultiplexerFactory != null || !string.IsNullOrWhiteSpace(options.Configuration),
                MissingConnectionMessage)
            .Validate(static options => options.ConnectionMultiplexerFactory != null || string.IsNullOrWhiteSpace(options.Configuration)
                || IsValidConfiguration(options.Configuration), InvalidConfigurationMessage)
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

    internal const string MissingConnectionMessage =
        "UseRedis has no Redis connection: set RedisOptions.Configuration to a StackExchange.Redis configuration string " +
        "(for example \"localhost:6379\") or set RedisOptions.ConnectionMultiplexerFactory. When the options are bound " +
        "from configuration, check that the section exists in this environment.";

    // The string itself is not repeated: it may hold a password
    internal const string InvalidConfigurationMessage =
        "RedisOptions.Configuration is not a valid StackExchange.Redis configuration string.";

    private static bool IsValidConfiguration(string configuration)
    {
        try { _ = StackExchange.Redis.ConfigurationOptions.Parse(configuration); return true; }
        catch (ArgumentException) { return false; }
    }
}

internal sealed record RedisRuntimeOptions(RedisOptions Options, string Prefix);
