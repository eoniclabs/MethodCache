using System.Text;

namespace MethodCache.PostgreSql;

/// <summary>
/// Options for using PostgreSQL as MethodCache's distributed cache.
/// </summary>
public sealed class PostgreSqlOptions
{
    // PostgreSQL truncates identifiers longer than NAMEDATALEN - 1 bytes; derived names add these suffixes
    internal const int MaxIdentifierBytes = 63;
    internal const string VersionTableSuffix = "_schema";
    internal const string CursorTableSuffix = "_cursor";
    internal const string ExpiresIndexSuffix = "_expires";
    internal const string CreatedIndexSuffix = "_created";

    /// <summary>
    /// The PostgreSQL connection string, in the format Npgsql reads. Required.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Resolves the connection string once from the completed container; takes precedence over ConnectionString.</summary>
    public Func<IServiceProvider, string>? ConnectionStringFactory { get; set; }

    /// <summary>
    /// The schema of the cache tables.
    /// </summary>
    public string SchemaName { get; set; } = "public";

    /// <summary>
    /// The table that holds cache entries. Its schema-version table is named after it, with <c>_schema</c> added.
    /// </summary>
    public string TableName { get; set; } = "methodcache";

    /// <summary>
    /// The table the backplane writes invalidations to. Its cursor table is named after it, with <c>_cursor</c> added.
    /// </summary>
    public string InvalidationsTableName { get; set; } = "methodcache_invalidations";

    /// <summary>
    /// Whether to create the schema and tables when the application starts, if they do not exist. Turn this off
    /// when the application's login may not create tables, and create them with the migration script in the package
    /// instead.
    /// </summary>
    public bool CreateTablesIfMissing { get; set; } = true;

    /// <summary>
    /// Whether to share invalidations between instances through the invalidations table, so other instances drop
    /// their in-memory copies. Without it they keep them until they expire.
    /// </summary>
    public bool EnableBackplane { get; set; } = true;

    /// <summary>
    /// How often each instance reads new invalidations. A notification (<c>LISTEN</c>/<c>NOTIFY</c>) makes it read
    /// them at once, so this is the longest delay when notifications are lost or cannot reach the instance.
    /// </summary>
    public TimeSpan BackplanePollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long invalidations are kept in the invalidations table; at least one minute. An instance that could not
    /// read the table for half this long may have missed invalidations, so it clears its in-memory cache. Instances
    /// that share a <c>KeyPrefix</c> keep invalidations as long as the longest retention any of them has used.
    /// </summary>
    public TimeSpan BackplaneRetention { get; set; } = TimeSpan.FromHours(1);

    internal string QualifiedTableName => Quote(SchemaName) + "." + Quote(TableName);

    internal string QualifiedVersionTableName => Quote(SchemaName) + "." + Quote(TableName + VersionTableSuffix);

    internal string QualifiedInvalidationsTableName => Quote(SchemaName) + "." + Quote(InvalidationsTableName);

    internal string QualifiedCursorTableName => Quote(SchemaName) + "." + Quote(InvalidationsTableName + CursorTableSuffix);

    /// <summary>Quotes an identifier, so any name is taken literally, with its case.</summary>
    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal void Validate()
    {
        if (ConnectionStringFactory == null && string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("PostgreSqlOptions.ConnectionString is required.");
        }

        if (string.IsNullOrWhiteSpace(SchemaName) || string.IsNullOrWhiteSpace(TableName) || string.IsNullOrWhiteSpace(InvalidationsTableName))
        {
            throw new InvalidOperationException("PostgreSqlOptions needs a schema, a table and an invalidations table name.");
        }

        if (SchemaName.Contains('\0', StringComparison.Ordinal) || TableName.Contains('\0', StringComparison.Ordinal) ||
            InvalidationsTableName.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("PostgreSQL identifiers cannot contain NUL characters.");
        }

        var longestTable = Math.Max(VersionTableSuffix.Length, ExpiresIndexSuffix.Length);
        var longestInvalidations = Math.Max(CursorTableSuffix.Length, CreatedIndexSuffix.Length);
        if (Encoding.UTF8.GetByteCount(SchemaName) > MaxIdentifierBytes ||
            Encoding.UTF8.GetByteCount(TableName) + longestTable > MaxIdentifierBytes ||
            Encoding.UTF8.GetByteCount(InvalidationsTableName) + longestInvalidations > MaxIdentifierBytes)
        {
            throw new InvalidOperationException(
                $"PostgreSQL identifiers, including the suffixes of the tables and indexes named after them, must fit {MaxIdentifierBytes} bytes.");
        }

        // Tables and indexes share one namespace per schema: a table named like another table's index would stop
        // that index from being created
        string[] relations =
        [
            TableName, TableName + VersionTableSuffix, TableName + ExpiresIndexSuffix,
            InvalidationsTableName, InvalidationsTableName + CursorTableSuffix, InvalidationsTableName + CreatedIndexSuffix
        ];
        if (relations.Distinct(StringComparer.Ordinal).Count() != relations.Length)
        {
            throw new InvalidOperationException("PostgreSQL cache, schema-version, invalidations and cursor tables and their indexes need distinct names.");
        }

        if (BackplanePollingInterval <= TimeSpan.Zero || BackplaneRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("PostgreSqlOptions.BackplanePollingInterval and BackplaneRetention must be positive.");
        }

        if (BackplaneRetention < TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("PostgreSqlOptions.BackplaneRetention must be at least one minute.");
        }
    }
}
