using MethodCache.Core.Storage;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Storage;

namespace MethodCache.Providers.SqlServer.Extensions;

/// <summary>
/// Extension methods for adding SQL Server providers to MethodCache.
/// </summary>
public static class MethodCacheBuilderExtensions
{
    /// <summary>
    /// Adds SQL Server as the L3 persistent cache layer.
    /// </summary>
    /// <param name="builder">The MethodCache builder.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithL3SqlServer(this IMethodCacheBuilder builder,
        string connectionString)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));

        return builder.WithL3(Storage.SqlServer.FromConnectionString(connectionString));
    }

    /// <summary>
    /// Adds SQL Server as the L3 persistent cache layer with custom configuration.
    /// </summary>
    /// <param name="builder">The MethodCache builder.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="configure">Configuration action for SQL Server options.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithL3SqlServer(this IMethodCacheBuilder builder,
        string connectionString, Action<SqlServerOptions> configure)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        if (configure == null) throw new ArgumentNullException(nameof(configure));

        return builder.WithL3(Storage.SqlServer.FromConnectionString(connectionString).Configure(configure));
    }

    /// <summary>
    /// Adds SQL Server as the L3 persistent cache layer with full configuration control.
    /// </summary>
    /// <param name="builder">The MethodCache builder.</param>
    /// <param name="configure">Configuration action for SQL Server options.</param>
    /// <returns>The builder for chaining.</returns>
    public static IMethodCacheBuilder WithL3SqlServer(this IMethodCacheBuilder builder,
        Action<SqlServerOptions> configure)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (configure == null) throw new ArgumentNullException(nameof(configure));

        return builder.WithL3(Storage.SqlServer.Configure(configure));
    }
}

/// <summary>
/// Extension methods for SQL Server provider configuration.
/// </summary>
public static class SqlServerProviderExtensions
{
    /// <summary>
    /// Configures the SQL Server provider.
    /// </summary>
    /// <param name="provider">The SQL Server provider.</param>
    /// <param name="configure">Configuration action.</param>
    /// <returns>The configured provider.</returns>
    public static SqlServerL3Provider Configure(this SqlServerL3Provider provider,
        Action<SqlServerOptions> configure)
    {
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        if (configure == null) throw new ArgumentNullException(nameof(configure));

        configure(provider.Options);
        return provider;
    }
}