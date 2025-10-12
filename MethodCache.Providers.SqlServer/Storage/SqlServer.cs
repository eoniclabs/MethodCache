using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.Storage;

/// <summary>
/// Factory for creating SQL Server L3 providers.
/// </summary>
public static class SqlServer
{
    /// <summary>
    /// Creates a SQL Server L3 provider from a connection string.
    /// </summary>
    /// <param name="connectionString">The SQL Server connection string.</param>
    /// <returns>A configured L3 SQL Server provider.</returns>
    public static SqlServerL3Provider FromConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

        return new SqlServerL3Provider { Options = { ConnectionString = connectionString } };
    }

    /// <summary>
    /// Creates a SQL Server L3 provider with custom configuration.
    /// </summary>
    /// <param name="configure">Configuration action.</param>
    /// <returns>A configured L3 SQL Server provider.</returns>
    public static SqlServerL3Provider Configure(Action<SqlServerOptions> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        return new SqlServerL3Provider().Configure(configure);
    }
}

/// <summary>
/// SQL Server L3 provider implementation.
/// </summary>
public class SqlServerL3Provider : IL3Provider
{
    internal SqlServerOptions Options { get; set; } = new();

    public string Name => "SqlServer";

    /// <summary>
    /// Configures the SQL Server provider options.
    /// </summary>
    /// <param name="configure">Configuration action.</param>
    /// <returns>This provider for chaining.</returns>
    public SqlServerL3Provider Configure(Action<SqlServerOptions> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        configure(Options);
        return this;
    }

    /// <summary>
    /// Sets the database schema for cache tables.
    /// </summary>
    /// <param name="schema">Database schema name.</param>
    /// <returns>This provider for chaining.</returns>
    public SqlServerL3Provider WithSchema(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema cannot be null or empty.", nameof(schema));
        Options.Schema = schema;
        return this;
    }

    /// <summary>
    /// Enables or disables automatic table creation.
    /// </summary>
    /// <param name="enabled">Whether to enable automatic table creation.</param>
    /// <returns>This provider for chaining.</returns>
    public SqlServerL3Provider WithAutoTableCreation(bool enabled = true)
    {
        Options.EnableAutoTableCreation = enabled;
        return this;
    }

    /// <summary>
    /// Sets the cleanup schedule for expired entries.
    /// </summary>
    /// <param name="interval">Cleanup interval.</param>
    /// <returns>This provider for chaining.</returns>
    public SqlServerL3Provider WithCleanupSchedule(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Cleanup interval must be greater than zero.");
        Options.CleanupInterval = interval;
        return this;
    }

    /// <summary>
    /// Enables the SQL Server backplane for distributed invalidation.
    /// </summary>
    /// <param name="enabled">Whether to enable the backplane.</param>
    /// <returns>This provider for chaining.</returns>
    public SqlServerL3Provider WithBackplane(bool enabled = true)
    {
        Options.EnableBackplane = enabled;
        return this;
    }

    public void Register(IServiceCollection services)
    {
        // Configure SQL Server options
        services.Configure<SqlServerOptions>(opt =>
        {
            opt.ConnectionString = Options.ConnectionString;
            opt.Schema = Options.Schema;
            opt.EnableAutoTableCreation = Options.EnableAutoTableCreation;
            opt.EnableBackplane = Options.EnableBackplane;
            opt.CleanupInterval = Options.CleanupInterval;
        });

        // Register SQL Server services
        services.TryAddSingleton<ISqlServerConnectionManager, SqlServerConnectionManager>();
        services.TryAddSingleton<ISqlServerSerializer, SqlServerSerializer>();
        services.TryAddSingleton<ISqlServerTableManager, SqlServerTableManager>();

        // Register the L3 persistent storage provider
        services.AddSingleton<SqlServerPersistentStorageProvider>();
        services.AddSingleton<IPersistentStorageProvider>(provider =>
            provider.GetRequiredService<SqlServerPersistentStorageProvider>());

        // Register backplane if enabled
        if (Options.EnableBackplane)
        {
            services.AddSingleton<SqlServerBackplane>();
            services.AddSingleton<IBackplane>(provider =>
                provider.GetRequiredService<SqlServerBackplane>());
        }
    }

    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Options.ConnectionString))
            throw new InvalidOperationException("SQL Server connection string is required.");

        if (string.IsNullOrWhiteSpace(Options.Schema))
            throw new InvalidOperationException("SQL Server schema is required.");

        if (Options.CleanupInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("SQL Server cleanup interval must be greater than zero.");
    }
}