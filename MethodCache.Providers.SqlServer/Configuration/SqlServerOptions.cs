using System.Data;

namespace MethodCache.Providers.SqlServer.Configuration;

/// <summary>
/// Configuration options for SQL Server cache storage provider.
/// </summary>
public class SqlServerOptions
{
    /// <summary>
    /// SQL Server connection string.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Database schema for cache tables. Default is 'cache'.
    /// </summary>
    public string Schema { get; set; } = "cache";

    /// <summary>
    /// Table name for cache entries. Default is 'Entries'.
    /// </summary>
    public string EntriesTableName { get; set; } = "Entries";

    /// <summary>
    /// Table name for tag associations. Default is 'Tags'.
    /// </summary>
    public string TagsTableName { get; set; } = "Tags";

    /// <summary>
    /// Table name for backplane invalidation messages. Default is 'Invalidations'.
    /// </summary>
    public string InvalidationsTableName { get; set; } = "Invalidations";

    /// <summary>
    /// Key prefix for cache entries. Default is 'methodcache:'.
    /// </summary>
    public string KeyPrefix { get; set; } = "methodcache:";

    /// <summary>
    /// Default serializer type for cache values.
    /// </summary>
    public SqlServerSerializerType DefaultSerializer { get; set; } = SqlServerSerializerType.MessagePack;

    /// <summary>
    /// Whether to enable automatic database table creation.
    /// </summary>
    public bool EnableAutoTableCreation { get; set; } = true;

    /// <summary>
    /// Command timeout in seconds for SQL operations. Default is 30 seconds.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Command timeout in seconds specifically for health check operations.
    /// Default is 5 seconds to quickly detect issues without blocking.
    /// Health checks should be lightweight and fast to avoid false negatives.
    /// </summary>
    public int HealthCheckTimeoutSeconds { get; set; } = 5;

    /// <summary>
    /// Connection timeout in seconds. Default is 15 seconds.
    /// </summary>
    public int ConnectionTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay between retry attempts.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Retry backoff type for handling transient failures.
    /// </summary>
    public SqlServerRetryBackoffType RetryBackoffType { get; set; } = SqlServerRetryBackoffType.Exponential;

    /// <summary>
    /// Circuit breaker failure ratio threshold (0.0 to 1.0).
    /// </summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>
    /// Minimum throughput for circuit breaker activation.
    /// </summary>
    public int CircuitBreakerMinimumThroughput { get; set; } = 10;

    /// <summary>
    /// Circuit breaker break duration.
    /// </summary>
    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Frequency of cleanup operations for expired entries.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of entries to delete in a single cleanup operation.
    /// </summary>
    public int CleanupBatchSize { get; set; } = 1000;

    /// <summary>
    /// Whether to enable background cleanup of expired entries.
    /// </summary>
    public bool EnableBackgroundCleanup { get; set; } = true;

    /// <summary>
    /// Whether to enable detailed performance logging.
    /// </summary>
    public bool EnableDetailedLogging { get; set; } = false;

    /// <summary>
    /// Isolation level for cache operations.
    /// </summary>
    public IsolationLevel IsolationLevel { get; set; } = IsolationLevel.ReadCommitted;

    /// <summary>
    /// Backplane polling interval for checking invalidation messages. Default is 2 seconds.
    /// </summary>
    public TimeSpan BackplanePollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to keep invalidation messages in the database. Default is 1 hour.
    /// </summary>
    public TimeSpan BackplaneMessageRetention { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether to enable the SQL Server backplane for distributed cache coordination.
    /// </summary>
    public bool EnableBackplane { get; set; } = false;

    /// <summary>
    /// Gets the full qualified table name for cache entries.
    /// </summary>
    public string FullEntriesTableName => $"[{Schema}].[{EntriesTableName}]";

    /// <summary>
    /// Gets the full qualified table name for tag associations.
    /// </summary>
    public string FullTagsTableName => $"[{Schema}].[{TagsTableName}]";

    /// <summary>
    /// Gets the full qualified table name for backplane invalidations.
    /// </summary>
    public string FullInvalidationsTableName => $"[{Schema}].[{InvalidationsTableName}]";
}

/// <summary>
/// Serializer types supported by SQL Server provider.
/// </summary>
public enum SqlServerSerializerType
{
    /// <summary>
    /// System.Text.Json serialization.
    /// </summary>
    Json,

    /// <summary>
    /// MessagePack binary serialization (recommended for performance).
    /// </summary>
    MessagePack,

    /// <summary>
    /// Binary serialization using BinaryFormatter.
    /// </summary>
    Binary
}

/// <summary>
/// Retry backoff strategies for transient failure handling.
/// </summary>
public enum SqlServerRetryBackoffType
{
    /// <summary>
    /// Linear backoff - constant delay between retries.
    /// </summary>
    Linear,

    /// <summary>
    /// Exponential backoff - delay doubles with each retry.
    /// </summary>
    Exponential
}