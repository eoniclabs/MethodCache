using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Core.Storage;

/// <summary>
/// Builder interface for configuring MethodCache with different storage layers.
/// </summary>
public interface IMethodCacheBuilder
{
    /// <summary>
    /// The service collection being configured.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Configures the L1 (memory) cache layer.
    /// </summary>
    /// <param name="provider">The L1 provider configuration.</param>
    /// <returns>The builder for chaining.</returns>
    IMethodCacheBuilder WithL1(IL1Provider provider);

    /// <summary>
    /// Configures the L2 (distributed) cache layer.
    /// </summary>
    /// <param name="provider">The L2 provider configuration.</param>
    /// <returns>The builder for chaining.</returns>
    IMethodCacheBuilder WithL2(IL2Provider provider);

    /// <summary>
    /// Configures the L3 (persistent) cache layer.
    /// </summary>
    /// <param name="provider">The L3 provider configuration.</param>
    /// <returns>The builder for chaining.</returns>
    IMethodCacheBuilder WithL3(IL3Provider provider);

    /// <summary>
    /// Configures cache layers from configuration.
    /// </summary>
    /// <param name="configuration">The configuration source.</param>
    /// <param name="sectionName">The configuration section name (default: "MethodCache").</param>
    /// <returns>The builder for chaining.</returns>
    IMethodCacheBuilder FromConfiguration(IConfiguration configuration, string sectionName = "MethodCache");

    /// <summary>
    /// Validates and finalizes the cache configuration.
    /// </summary>
    /// <returns>The service collection for further configuration.</returns>
    IServiceCollection Build();
}

/// <summary>
/// Configuration options for method cache.
/// </summary>
public class MethodCacheConfiguration
{
    /// <summary>
    /// L1 cache configuration.
    /// </summary>
    public L1Configuration? L1 { get; set; }

    /// <summary>
    /// L2 cache configuration.
    /// </summary>
    public L2Configuration? L2 { get; set; }

    /// <summary>
    /// L3 cache configuration.
    /// </summary>
    public L3Configuration? L3 { get; set; }

    /// <summary>
    /// Health checks configuration.
    /// </summary>
    public HealthChecksConfiguration? HealthChecks { get; set; }

    /// <summary>
    /// Metrics configuration.
    /// </summary>
    public MetricsConfiguration? Metrics { get; set; }
}

/// <summary>
/// L1 cache configuration.
/// </summary>
public class L1Configuration
{
    /// <summary>
    /// Whether L1 cache is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of items in memory.
    /// </summary>
    public int MaxSize { get; set; } = 10000;

    /// <summary>
    /// Default expiration for L1 items.
    /// </summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// L2 cache configuration.
/// </summary>
public class L2Configuration
{
    /// <summary>
    /// Whether L2 cache is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Connection string for the distributed cache.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Provider type (e.g., "Redis").
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Key prefix for cache entries.
    /// </summary>
    public string? KeyPrefix { get; set; }
}

/// <summary>
/// L3 cache configuration.
/// </summary>
public class L3Configuration
{
    /// <summary>
    /// Whether L3 cache is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Connection string for the persistent storage.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Provider type (e.g., "SqlServer").
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Database schema for cache tables.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Whether to automatically create database tables.
    /// </summary>
    public bool AutoTableCreation { get; set; } = true;
}

/// <summary>
/// Health checks configuration.
/// </summary>
public class HealthChecksConfiguration
{
    /// <summary>
    /// Whether health checks are enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Health check name.
    /// </summary>
    public string Name { get; set; } = "methodcache";
}

/// <summary>
/// Metrics configuration.
/// </summary>
public class MetricsConfiguration
{
    /// <summary>
    /// Whether metrics are enabled.
    /// </summary>
    public bool Enabled { get; set; }
}