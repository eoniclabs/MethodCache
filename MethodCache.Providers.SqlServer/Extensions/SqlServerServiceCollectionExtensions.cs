using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Infrastructure.Implementation;
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
        // Add core infrastructure first
        services.AddCacheInfrastructure();

        // Configure options
        if (configureSqlServer != null)
        {
            services.Configure(configureSqlServer);
        }

        // Register SQL Server-specific services
        services.TryAddSingleton<ISqlServerConnectionManager, SqlServerConnectionManager>();
        services.TryAddSingleton<ISqlServerSerializer, SqlServerSerializer>();
        services.TryAddSingleton<ISqlServerTableManager, SqlServerTableManager>();

        // Register the L3 persistent storage provider
        services.AddSingleton<SqlServerPersistentStorageProvider>();
        services.AddSingleton<IPersistentStorageProvider>(provider => provider.GetRequiredService<SqlServerPersistentStorageProvider>());
        // Also register as IStorageProvider for backward compatibility
        services.AddSingleton<IStorageProvider>(provider => provider.GetRequiredService<SqlServerPersistentStorageProvider>());

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
        // Add SQL Server L3 infrastructure
        services.AddSqlServerInfrastructure(configureSqlServer);

        // Add core infrastructure services
        services.AddCacheInfrastructure();

        // Register HybridStorageManager with L1 + L3 (SqlServer as persistent storage)
        services.TryAddSingleton<HybridStorageManager>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var options = provider.GetRequiredService<IOptions<StorageOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridStorageManager>>();
            var l3Storage = provider.GetRequiredService<IPersistentStorageProvider>();
            var backplane = provider.GetService<IBackplane>();

            return new HybridStorageManager(memoryStorage, options, logger, l2Storage: null, l3Storage, backplane);
        });

        // Register as IStorageProvider for backward compatibility
        services.TryAddSingleton<IStorageProvider>(provider => provider.GetRequiredService<HybridStorageManager>());

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
    /// Adds hybrid L1/L3 cache with SQL Server as L3 persistent and in-memory as L1.
    /// Uses the Infrastructure layer for provider-agnostic implementation.
    /// </summary>
    public static IServiceCollection AddHybridSqlServerCache(
        this IServiceCollection services,
        Action<MethodCache.Core.Storage.HybridCacheOptions> configureHybridOptions,
        Action<SqlServerOptions>? configureSqlServerOptions = null)
    {
        if (configureHybridOptions == null)
            throw new ArgumentNullException(nameof(configureHybridOptions));

        // Add SQL Server infrastructure with L1+L3 storage
        services.AddSqlServerHybridInfrastructure(configureSqlServerOptions);

        // Configure hybrid cache options for L1+L3
        if (configureHybridOptions != null)
        {
            services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(options =>
            {
                configureHybridOptions(options);
                options.L3Enabled = true;
            });
        }

        return services;
    }

    /// <summary>
    /// Adds hybrid L1/L3 cache with SQL Server and custom configuration.
    /// This is the comprehensive setup method for SQL Server hybrid caching.
    /// </summary>
    public static IServiceCollection AddHybridSqlServerCache(
        this IServiceCollection services,
        string connectionString,
        Action<MethodCache.Core.Storage.HybridCacheOptions>? configureHybridOptions = null,
        Action<SqlServerOptions>? configureSqlServerOptions = null)
    {
        // Add SQL Server infrastructure
        services.AddSqlServerHybridInfrastructure(options =>
        {
            options.ConnectionString = connectionString;
            configureSqlServerOptions?.Invoke(options);
        });

        // Configure hybrid cache options for L1+L3
        if (configureHybridOptions != null)
        {
            services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(options =>
            {
                configureHybridOptions(options);
                options.L3Enabled = true;
            });
        }

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

        // Configure hybrid cache options
        services.Configure<MethodCache.Core.Storage.HybridCacheOptions>(hybrid =>
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

    // L3 Persistent Cache Extension Methods

    /// <summary>
    /// Adds SQL Server as L3 persistent cache provider.
    /// This provides long-term cache persistence with automatic cleanup and large storage capacity.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureSqlServer">Configuration for SQL Server options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSqlServerPersistentCache(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureSqlServer = null)
    {
        // Add SQL Server infrastructure for L3
        services.AddSqlServerInfrastructure(configureSqlServer);

        return services;
    }

    /// <summary>
    /// Adds complete triple-layer cache (L1 + L2 + L3) with SQL Server as L3 persistent storage.
    /// This provides the full caching stack: Memory (L1) + Distributed (L2) + Persistent (L3).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureStorage">Configuration for storage options.</param>
    /// <param name="configureSqlServer">Configuration for SQL Server L3 options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTripleLayerCacheWithSqlServer(
        this IServiceCollection services,
        Action<StorageOptions>? configureStorage = null,
        Action<SqlServerOptions>? configureSqlServer = null)
    {
        // Add infrastructure services
        services.AddCacheInfrastructure(configureStorage);

        // Add SQL Server as L3 persistent storage
        services.AddSqlServerPersistentCache(configureSqlServer);

        // Register the enhanced hybrid storage manager with L3 support
        services.TryAddSingleton<HybridStorageManager>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var options = provider.GetRequiredService<IOptions<StorageOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridStorageManager>>();
            var l2Storage = provider.GetService<IStorageProvider>(); // Redis or other L2
            var l3Storage = provider.GetRequiredService<IPersistentStorageProvider>(); // SQL Server L3
            var backplane = provider.GetService<IBackplane>();

            return new HybridStorageManager(memoryStorage, options, logger, l2Storage, l3Storage, backplane);
        });

        // Register as IStorageProvider
        services.TryAddSingleton<IStorageProvider>(provider => provider.GetRequiredService<HybridStorageManager>());

        return services;
    }

    /// <summary>
    /// Adds L1 + L3 cache setup (skipping L2 distributed cache).
    /// Useful for single-instance applications that need persistence without distribution complexity.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureStorage">Configuration for storage options.</param>
    /// <param name="configureSqlServer">Configuration for SQL Server L3 options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddL1L3CacheWithSqlServer(
        this IServiceCollection services,
        Action<StorageOptions>? configureStorage = null,
        Action<SqlServerOptions>? configureSqlServer = null)
    {
        // Add infrastructure services
        services.AddCacheInfrastructure(options =>
        {
            options.L2Enabled = false; // Skip L2
            configureStorage?.Invoke(options);
        });

        // Add SQL Server as L3 persistent storage
        services.AddSqlServerPersistentCache(configureSqlServer);

        // Register the enhanced hybrid storage manager (L1 + L3, no L2)
        services.TryAddSingleton<HybridStorageManager>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var options = provider.GetRequiredService<IOptions<StorageOptions>>();
            var logger = provider.GetRequiredService<ILogger<HybridStorageManager>>();
            var l3Storage = provider.GetRequiredService<IPersistentStorageProvider>(); // SQL Server L3

            return new HybridStorageManager(memoryStorage, options, logger, null, l3Storage, null);
        });

        // Register as IStorageProvider
        services.TryAddSingleton<IStorageProvider>(provider => provider.GetRequiredService<HybridStorageManager>());

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
    public MethodCache.Core.Storage.HybridStrategy Strategy { get; set; } = MethodCache.Core.Storage.HybridStrategy.WriteThrough;

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