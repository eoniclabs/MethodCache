using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using MethodCache.HybridCache.Extensions;
using MethodCache.HybridCache.Configuration;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.HealthChecks;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.Extensions;

/// <summary>
/// Extension methods for configuring SQL Server cache providers.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Adds SQL Server Infrastructure for caching with the specified options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureSqlServer">Configuration for SQL Server options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerInfrastructure(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureSqlServer = null)
    {
        // Configure options
        if (configureSqlServer != null)
        {
            services.Configure(configureSqlServer);
        }

        // Register SQL Server-specific services
        services.TryAddSingleton<ISqlServerConnectionManager, SqlServerConnectionManager>();
        services.TryAddSingleton<ISqlServerSerializer, SqlServerSerializer>();
        services.TryAddSingleton<ISqlServerTableManager, SqlServerTableManager>();

        // Register the storage provider
        services.AddSingleton<SqlServerStorageProvider>();
        services.AddSingleton<IStorageProvider>(provider => provider.GetRequiredService<SqlServerStorageProvider>());

        // Register the backplane (conditionally based on options)
        services.AddSingleton<SqlServerBackplane>();
        services.AddSingleton<IBackplane>(provider => provider.GetRequiredService<SqlServerBackplane>());

        return services;
    }

    /// <summary>
    /// Adds SQL Server Infrastructure with hybrid storage manager for L1+L2 caching.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureSqlServer">Configuration for SQL Server options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerHybridInfrastructure(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureSqlServer = null)
    {
        // For now, just add the basic SQL Server infrastructure
        // TODO: Implement full hybrid storage manager registration in future iteration
        services.AddSqlServerInfrastructure(configureSqlServer);

        // Add basic infrastructure services for L1 cache
        services.AddCacheInfrastructure();

        return services;
    }

    /// <summary>
    /// Adds SQL Server Infrastructure with health checks.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureSqlServer">Configuration for SQL Server options.</param>
    /// <param name="healthCheckName">Name for the health check (default: "sql_server_infrastructure").</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerInfrastructureWithHealthChecks(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureSqlServer = null,
        string healthCheckName = "sql_server_infrastructure")
    {
        services.AddSqlServerInfrastructure(configureSqlServer);

        // Add health checks
        services.AddHealthChecks()
            .AddCheck<SqlServerInfrastructureHealthCheck>(healthCheckName);
        services.TryAddSingleton<SqlServerInfrastructureHealthCheck>();

        return services;
    }

    /// <summary>
    /// Adds SQL Server cache services with MethodCache core integration.
    /// This is a convenience method that registers both MethodCache and SQL Server Infrastructure.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional SQL Server configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerCache(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureOptions = null)
    {
        // Ensure MethodCache core is registered
        services.AddMethodCache();

        // Add SQL Server infrastructure
        services.AddSqlServerInfrastructure(configureOptions);

        return services;
    }

    /// <summary>
    /// Adds hybrid L1/L2 cache with SQL Server as L2 and in-memory as L1.
    /// Uses the Infrastructure layer for provider-agnostic implementation.
    /// </summary>
    public static IServiceCollection AddHybridSqlServerCache(
        this IServiceCollection services,
        Action<HybridCacheOptions> configureHybridOptions,
        Action<SqlServerOptions>? configureSqlServerOptions = null)
    {
        if (configureHybridOptions == null)
            throw new ArgumentNullException(nameof(configureHybridOptions));

        // Add SQL Server infrastructure with L1+L2 storage
        services.AddSqlServerHybridInfrastructure(configureSqlServerOptions);

        // Add hybrid cache using Infrastructure
        services.AddInfrastructureHybridCacheWithL2(configureHybridOptions);

        return services;
    }

    /// <summary>
    /// Adds hybrid L1/L2 cache with SQL Server and custom configuration.
    /// This is the comprehensive setup method for SQL Server hybrid caching.
    /// </summary>
    public static IServiceCollection AddHybridSqlServerCache(
        this IServiceCollection services,
        string connectionString,
        Action<HybridCacheOptions>? configureHybridOptions = null,
        Action<SqlServerOptions>? configureSqlServerOptions = null)
    {
        // Add SQL Server infrastructure
        services.AddSqlServerHybridInfrastructure(options =>
        {
            options.ConnectionString = connectionString;
            configureSqlServerOptions?.Invoke(options);
        });

        // Add hybrid cache using Infrastructure
        services.AddInfrastructureHybridCacheWithL2(configureHybridOptions);

        return services;
    }

    /// <summary>
    /// Adds the complete SQL Server + Hybrid Cache stack in one call.
    /// This is the recommended way to set up hybrid caching with SQL Server using Infrastructure.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="configure">Optional configuration action.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerHybridCacheComplete(
        this IServiceCollection services,
        string connectionString,
        Action<SqlServerHybridCacheOptions>? configure = null)
    {
        var options = new SqlServerHybridCacheOptions();
        configure?.Invoke(options);

        // Add SQL Server infrastructure
        services.AddSqlServerHybridInfrastructure(sqlServer =>
        {
            sqlServer.ConnectionString = connectionString;
            sqlServer.Schema = options.Schema;
            sqlServer.DefaultSerializer = options.SqlServerSerializer;
            sqlServer.EnableAutoTableCreation = options.EnableAutoTableCreation;
            sqlServer.CommandTimeoutSeconds = options.CommandTimeoutSeconds;
            sqlServer.MaxRetryAttempts = options.MaxRetryAttempts;
            sqlServer.EnableBackplane = options.EnableBackplane;
            sqlServer.BackplanePollingInterval = options.BackplanePollingInterval;
            sqlServer.BackplaneMessageRetention = options.BackplaneMessageRetention;
        });

        // Add hybrid cache
        services.AddInfrastructureHybridCacheWithL2(hybrid =>
        {
            hybrid.L1DefaultExpiration = options.L1DefaultExpiration;
            hybrid.L1MaxExpiration = options.L1MaxExpiration;
            hybrid.L2DefaultExpiration = options.L2DefaultExpiration;
            hybrid.L2Enabled = true;
            hybrid.Strategy = options.Strategy;
            hybrid.EnableBackplane = options.EnableBackplane;
            hybrid.EnableAsyncL2Writes = options.EnableAsyncL2Writes;
        });

        return services;
    }
}

/// <summary>
/// Configuration options for SQL Server + Hybrid Cache setup.
/// </summary>
public class SqlServerHybridCacheOptions
{
    /// <summary>
    /// Default expiration time for L1 cache entries.
    /// </summary>
    public TimeSpan L1DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum expiration time for L1 cache entries.
    /// </summary>
    public TimeSpan L1MaxExpiration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Default expiration time for L2 cache entries.
    /// </summary>
    public TimeSpan L2DefaultExpiration { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Hybrid cache strategy.
    /// </summary>
    public HybridStrategy Strategy { get; set; } = HybridStrategy.WriteThrough;

    /// <summary>
    /// Whether to enable backplane coordination.
    /// </summary>
    public bool EnableBackplane { get; set; } = true;

    /// <summary>
    /// Backplane polling interval for checking invalidation messages.
    /// </summary>
    public TimeSpan BackplanePollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to keep backplane messages in the database.
    /// </summary>
    public TimeSpan BackplaneMessageRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether to enable async L2 writes.
    /// </summary>
    public bool EnableAsyncL2Writes { get; set; } = true;

    /// <summary>
    /// SQL Server database schema.
    /// </summary>
    public string Schema { get; set; } = "cache";

    /// <summary>
    /// SQL Server serializer type.
    /// </summary>
    public SqlServerSerializerType SqlServerSerializer { get; set; } = SqlServerSerializerType.MessagePack;

    /// <summary>
    /// Whether to enable automatic table creation.
    /// </summary>
    public bool EnableAutoTableCreation { get; set; } = true;

    /// <summary>
    /// SQL command timeout in seconds.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum retry attempts for transient failures.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;
}