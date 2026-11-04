using MethodCache;
using MethodCache.Extensibility;
using MethodCache.PostgreSql;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers PostgreSQL as MethodCache's distributed cache.
/// </summary>
public static class PostgreSqlMethodCacheBuilderExtensions
{
    /// <summary>
    /// Uses PostgreSQL as MethodCache's distributed cache: results are shared between instances, and invalidations
    /// reach every instance's in-memory cache through a table, announced with <c>LISTEN</c>/<c>NOTIFY</c>.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddMethodCache(mc =&gt; ...)</c>.</param>
    /// <param name="connectionString">The PostgreSQL connection string, in the format Npgsql reads.</param>
    /// <returns>The builder, for chaining.</returns>
    public static MethodCache.MethodCacheBuilder UsePostgreSql(this MethodCache.MethodCacheBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        AddPostgreSql(builder.Services, options => options.ConnectionString = connectionString);
        return builder;
    }

    /// <summary>
    /// Uses PostgreSQL as MethodCache's distributed cache, with explicit connection, table and backplane options.
    /// </summary>
    /// <param name="builder">The builder passed to <c>AddMethodCache(mc =&gt; ...)</c>.</param>
    /// <param name="configure">Configures the connection, the tables and the backplane.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// MethodCache keeps its own connection pool, so the app's data sources and <c>IDistributedCache</c> are left
    /// alone. Startup never waits long for PostgreSQL: missing tables are created in the background, retried until the
    /// database answers.
    /// </remarks>
    public static MethodCache.MethodCacheBuilder UsePostgreSql(this MethodCache.MethodCacheBuilder builder, Action<PostgreSqlOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        AddPostgreSql(builder.Services, configure);
        return builder;
    }

    private static IServiceCollection AddPostgreSql(IServiceCollection services, Action<PostgreSqlOptions> configure)
    {
        MethodCache.Infrastructure.OwnedServices.AddOptions(services);
        services.AddOptions<PostgreSqlOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PostgreSqlOptions>, PostgreSqlOptionsValidator>());
        services.TryAddSingleton(static sp =>
        {
            var configured = sp.GetRequiredService<IOptions<PostgreSqlOptions>>().Value;
            var options = new PostgreSqlOptions
            {
                ConnectionString = configured.ConnectionStringFactory?.Invoke(sp) ?? configured.ConnectionString,
                ConfigureDataSource = configured.ConfigureDataSource,
                SchemaName = configured.SchemaName,
                TableName = configured.TableName,
                InvalidationsTableName = configured.InvalidationsTableName,
                CreateTablesIfMissing = configured.CreateTablesIfMissing,
                EnableBackplane = configured.EnableBackplane,
                BackplanePollingInterval = configured.BackplanePollingInterval,
                BackplaneRetention = configured.BackplaneRetention
            };
            options.Validate();
            _ = new NpgsqlConnectionStringBuilder(options.ConnectionString);
            return new PostgreSqlRuntimeOptions(options,
                sp.GetRequiredKeyedService<DistributedCacheOptions>(MethodCache.Infrastructure.OwnedServices.Key).KeyPrefix);
        });
        // One pool for the cache, the tables and the backplane; the container disposes it
        services.TryAddSingleton(static sp =>
        {
            var options = sp.GetRequiredService<PostgreSqlRuntimeOptions>().Options;
            return new PostgreSqlConnection(options.ConnectionString, options.ConfigureDataSource);
        });
        // UsePostgreSql may be called again to add to the options; the tables are still created once
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PostgreSqlTables>(static sp => new PostgreSqlTables(
            sp.GetRequiredService<PostgreSqlConnection>(),
            sp.GetRequiredService<PostgreSqlRuntimeOptions>().Options,
            sp.GetService<ILoggerFactory>()?.CreateLogger<PostgreSqlTables>(), timeProvider: sp.GetService<TimeProvider>())));
        services.TryAddKeyedSingleton<IBackplane>(MethodCache.Infrastructure.OwnedServices.Key, static (sp, _) =>
        {
            var runtime = sp.GetRequiredService<PostgreSqlRuntimeOptions>();
            return runtime.Options.EnableBackplane ? new PostgreSqlBackplane(sp.GetRequiredService<PostgreSqlConnection>(), runtime.Options,
                sp.GetService<ILoggerFactory>()?.CreateLogger<PostgreSqlBackplane>(), sp.GetService<TimeProvider>(), runtime.Prefix) : null!;
        });
        return services.AddMethodCacheDistributedCache(static sp => new PostgreSqlDistributedCache(
            sp.GetRequiredService<PostgreSqlConnection>(), sp.GetRequiredService<PostgreSqlRuntimeOptions>().Options));
    }
}

internal sealed record PostgreSqlRuntimeOptions(PostgreSqlOptions Options, string Prefix);

/// <summary>Fails startup with the message of the rule the options break, not a summary of every rule.</summary>
internal sealed class PostgreSqlOptionsValidator : IValidateOptions<PostgreSqlOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, PostgreSqlOptions options)
    {
        // UsePostgreSql configures the unnamed options; named ones are not MethodCache's
        if (name != Microsoft.Extensions.Options.Options.DefaultName) return ValidateOptionsResult.Skip;
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException error)
        {
            return ValidateOptionsResult.Fail(error.Message);
        }
    }
}
