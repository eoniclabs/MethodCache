using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.Core.Storage;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Extensions;

/// <summary>
/// Extension methods for configuring MethodCache services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds MethodCache services with a default memory L1 provider.
    /// This is the recommended starting point for most applications.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A builder for configuring additional cache layers.</returns>
    public static IMethodCacheBuilder AddMethodCache(this IServiceCollection services)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        // Register core MethodCache services
        RegisterCoreServices(services);

        // Create builder with default L1 memory provider
        var builder = new Storage.MethodCacheBuilder(services);

        // Add default memory L1 provider
        return builder.WithL1(Memory.Default());
    }

    /// <summary>
    /// Adds MethodCache core services without any providers.
    /// Use this when you want full control over provider configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A builder for configuring cache layers.</returns>
    public static IMethodCacheBuilder AddMethodCacheCore(this IServiceCollection services)
    {
        if (services == null) throw new ArgumentNullException(nameof(services));

        // Register core MethodCache services
        RegisterCoreServices(services);

        return new Storage.MethodCacheBuilder(services);
    }

    private static void RegisterCoreServices(IServiceCollection services)
    {
        // Register core cache manager
        services.TryAddSingleton<ICacheManager, InMemoryCacheManager>();

        // Register default storage options
        services.TryAddSingleton<StorageOptions>();

        // Register default memory storage implementation
        services.TryAddSingleton<IMemoryStorage, MemoryStorage>();

        // Register default cache key generator
        services.TryAddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();
    }
}

/// <summary>
/// Extension methods for conditional cache configuration.
/// </summary>
public static class ConditionalExtensions
{
    /// <summary>
    /// Conditionally adds an L2 provider based on a condition.
    /// </summary>
    /// <param name="builder">The cache builder.</param>
    /// <param name="condition">The condition to evaluate.</param>
    /// <param name="providerFactory">Factory function to create the provider.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithL2If(this IMethodCacheBuilder builder,
        bool condition, Func<IL2Provider> providerFactory)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (providerFactory == null) throw new ArgumentNullException(nameof(providerFactory));

        return condition ? builder.WithL2(providerFactory()) : builder;
    }

    /// <summary>
    /// Conditionally adds an L3 provider based on a condition.
    /// </summary>
    /// <param name="builder">The cache builder.</param>
    /// <param name="condition">The condition to evaluate.</param>
    /// <param name="providerFactory">Factory function to create the provider.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithL3If(this IMethodCacheBuilder builder,
        bool condition, Func<IL3Provider> providerFactory)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (providerFactory == null) throw new ArgumentNullException(nameof(providerFactory));

        return condition ? builder.WithL3(providerFactory()) : builder;
    }

    /// <summary>
    /// Conditionally configures the cache based on an environment variable.
    /// </summary>
    /// <param name="builder">The cache builder.</param>
    /// <param name="environmentVariable">The environment variable name.</param>
    /// <param name="expectedValue">The expected value for the condition to be true.</param>
    /// <param name="configureAction">Action to configure the cache when condition is met.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithEnvironmentCondition(this IMethodCacheBuilder builder,
        string environmentVariable, string expectedValue, Action<IMethodCacheBuilder> configureAction)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrEmpty(environmentVariable)) throw new ArgumentException("Environment variable name cannot be null or empty.", nameof(environmentVariable));
        if (configureAction == null) throw new ArgumentNullException(nameof(configureAction));

        var actualValue = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.Equals(actualValue, expectedValue, StringComparison.OrdinalIgnoreCase))
        {
            configureAction(builder);
        }

        return builder;
    }
}