using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MethodCache.SqlServer;

public static class SqlServerServiceCollectionExtensions
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
    public static IServiceCollection AddMethodCacheSqlServer(this IServiceCollection services, Action<SqlServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqlServerOptions();
        configure(options);
        options.Validate();

        services.AddDistributedSqlServerCache(cacheOptions =>
        {
            cacheOptions.ConnectionString = options.ConnectionString;
            cacheOptions.SchemaName = options.SchemaName;
            cacheOptions.TableName = options.TableName;
        });

        if (options.CreateTablesIfMissing)
        {
            services.AddSingleton<IHostedService>(new SqlServerTables(options));
        }

        if (options.EnableBackplane)
        {
            services.TryAddSingleton<IBackplane>(_ => new SqlServerBackplane(options));
        }

        return services.AddMethodCacheDistributedCache(cacheOptions => cacheOptions.KeyPrefix = options.KeyPrefix);
    }
}
