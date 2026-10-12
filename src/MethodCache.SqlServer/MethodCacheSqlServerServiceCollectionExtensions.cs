using MethodCache.Extensibility;
using MethodCache.SqlServer;
using Microsoft.Extensions.Caching.SqlServer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers SQL Server as MethodCache's distributed cache.
/// </summary>
public static class MethodCacheSqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Uses SQL Server as MethodCache's distributed cache: results are shared between instances, and invalidations
    /// reach every instance's in-memory cache through a table they poll. Call <c>AddMethodCache()</c> as well.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="connectionString">The SQL Server connection string</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddMethodCacheSqlServer(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        return services.AddMethodCacheSqlServer(options => options.ConnectionString = connectionString);
    }

    /// <summary>
    /// Uses SQL Server as MethodCache's distributed cache: results are shared between instances, and invalidations
    /// reach every instance's in-memory cache through a table they poll. Call <c>AddMethodCache()</c> as well.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configures the connection, the tables and the backplane</param>
    /// <returns>The service collection for chaining</returns>
    /// <remarks>
    /// MethodCache keeps its own cache instance, so the app's <c>IDistributedCache</c> is left alone. Startup never
    /// waits for SQL Server: missing tables are created in the background, retried until the database answers.
    /// </remarks>
    public static IServiceCollection AddMethodCacheSqlServer(this IServiceCollection services, Action<SqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqlServerOptions();
        configure(options);
        options.Validate();

        if (options.CreateTablesIfMissing)
        {
            services.AddSingleton<IHostedService>(sp => new SqlServerTables(options, sp.GetService<ILoggerFactory>()?.CreateLogger<SqlServerTables>()));
        }

        if (options.EnableBackplane)
        {
            services.TryAddSingleton<IBackplane>(sp => new SqlServerBackplane(options, sp.GetService<ILoggerFactory>()?.CreateLogger<SqlServerBackplane>(), sp.GetService<TimeProvider>()));
        }

        // MethodCache's own cache instance, so the app's IDistributedCache and its settings are left alone
        return services.AddMethodCacheDistributedCache(
            _ => new SqlServerDistributedCache(options),
            cacheOptions => cacheOptions.KeyPrefix = options.KeyPrefix);
    }

    internal static SqlServerCacheOptions CacheOptions(SqlServerOptions options) => new()
    {
        ConnectionString = options.ConnectionString,
        SchemaName = options.SchemaName,
        TableName = options.TableName,
    };
}
