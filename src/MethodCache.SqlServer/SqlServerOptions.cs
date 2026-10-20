namespace MethodCache.SqlServer;

/// <summary>
/// Options for using SQL Server as MethodCache's distributed cache.
/// </summary>
public sealed class SqlServerOptions
{
    /// <summary>
    /// The SQL Server connection string. Required.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Resolves the connection string once from the completed container; takes precedence over ConnectionString.</summary>
    public Func<IServiceProvider, string>? ConnectionStringFactory { get; set; }

    /// <summary>
    /// The schema of the cache tables.
    /// </summary>
    public string SchemaName { get; set; } = "dbo";

    /// <summary>
    /// The table that holds cache entries, in the layout <c>Microsoft.Extensions.Caching.SqlServer</c> uses.
    /// </summary>
    public string TableName { get; set; } = "MethodCache";

    /// <summary>
    /// The table the backplane writes invalidations to.
    /// </summary>
    public string InvalidationsTableName { get; set; } = "MethodCacheInvalidations";

    /// <summary>
    /// Whether to create the schema and tables when the application starts, if they do not exist. Turn this off
    /// when the application's login may not create tables, and create them with a migration instead.
    /// </summary>
    public bool CreateTablesIfMissing { get; set; } = true;

    /// <summary>
    /// Whether to share invalidations between instances through the invalidations table, so other instances drop
    /// their in-memory copies within one polling interval. Without it they keep them until they expire.
    /// </summary>
    public bool EnableBackplane { get; set; } = true;

    /// <summary>
    /// How often each instance reads new invalidations.
    /// </summary>
    public TimeSpan BackplanePollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long invalidations are kept in the invalidations table; at least one minute. An instance that could not
    /// read the table for half this long may have missed invalidations, so it clears its in-memory cache.
    /// </summary>
    public TimeSpan BackplaneRetention { get; set; } = TimeSpan.FromHours(1);

    internal string QualifiedVersionTableName => Quote(SchemaName) + "." + Quote(TableName + "Schema");

    internal string QualifiedTableName => Quote(SchemaName) + "." + Quote(TableName);

    internal string QualifiedInvalidationsTableName => Quote(SchemaName) + "." + Quote(InvalidationsTableName);

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal void Validate()
    {
        if (ConnectionStringFactory == null && string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("SqlServerOptions.ConnectionString is required.");
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || string.IsNullOrWhiteSpace(TableName) || string.IsNullOrWhiteSpace(InvalidationsTableName))
        {
            throw new InvalidOperationException("SqlServerOptions needs a schema, a table and an invalidations table name.");
        }

        if (SchemaName.Length > 128 || TableName.Length > 122 || InvalidationsTableName.Length > 128)
            throw new InvalidOperationException("SQL Server table identifiers, including the schema-version table suffix, must fit 128 characters.");

        if (string.Equals(TableName, InvalidationsTableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(TableName + "Schema", InvalidationsTableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQL Server cache, schema-version and invalidations tables need distinct names.");

        if (BackplanePollingInterval <= TimeSpan.Zero || BackplaneRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SqlServerOptions.BackplanePollingInterval and BackplaneRetention must be positive.");
        }

        if (BackplaneRetention < TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("SqlServerOptions.BackplaneRetention must be at least one minute.");
        }
    }
}
