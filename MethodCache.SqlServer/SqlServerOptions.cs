namespace MethodCache.SqlServer;

/// <summary>
/// Options for using SQL Server as MethodCache's distributed cache.
/// </summary>
public class SqlServerOptions
{
    /// <summary>
    /// The SQL Server connection string. Required.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

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
    /// Prefix for every key MethodCache writes, so several applications can share one table.
    /// </summary>
    public string KeyPrefix { get; set; } = "methodcache:";

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
    /// How long invalidations are kept in the invalidations table.
    /// </summary>
    public TimeSpan BackplaneRetention { get; set; } = TimeSpan.FromHours(1);

    internal string QualifiedTableName => Quote(SchemaName) + "." + Quote(TableName);

    internal string QualifiedInvalidationsTableName => Quote(SchemaName) + "." + Quote(InvalidationsTableName);

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("SqlServerOptions.ConnectionString is required.");
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || string.IsNullOrWhiteSpace(TableName) || string.IsNullOrWhiteSpace(InvalidationsTableName))
        {
            throw new InvalidOperationException("SqlServerOptions needs a schema, a table and an invalidations table name.");
        }

        if (BackplanePollingInterval <= TimeSpan.Zero || BackplaneRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SqlServerOptions.BackplanePollingInterval and BackplaneRetention must be positive.");
        }
    }
}
