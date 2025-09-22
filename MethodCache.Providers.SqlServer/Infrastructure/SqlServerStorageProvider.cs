using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Services;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace MethodCache.Providers.SqlServer.Infrastructure;

/// <summary>
/// SQL Server implementation of IPersistentStorageProvider for L3 persistent cache storage.
/// Provides persistent storage capabilities with automatic table management, retry policies, health monitoring, and cleanup operations.
/// Designed for long-term data persistence with durability and large storage capacity.
/// </summary>
public class SqlServerPersistentStorageProvider : IPersistentStorageProvider, IStorageProvider, IAsyncDisposable
{
    private readonly ISqlServerConnectionManager _connectionManager;
    private readonly ISqlServerSerializer _serializer;
    private readonly ISqlServerTableManager _tableManager;
    private readonly IBackplane? _backplane;
    private readonly ResiliencePipeline _resilience;
    private readonly SqlServerOptions _options;
    private readonly ILogger<SqlServerPersistentStorageProvider> _logger;
    private readonly Timer? _cleanupTimer;

    // Statistics
    private long _getOperations;
    private long _setOperations;
    private long _removeOperations;
    private long _errorCount;
    private readonly object _statsLock = new();
    private DateTime _lastOperationTime = DateTime.UtcNow;

    // Disposal tracking
    private bool _disposed;

    public string Name => "SqlServer-L3-Persistent";

    public SqlServerPersistentStorageProvider(
        ISqlServerConnectionManager connectionManager,
        ISqlServerSerializer serializer,
        ISqlServerTableManager tableManager,
        IBackplane? backplane,
        IOptions<SqlServerOptions> options,
        ILogger<SqlServerPersistentStorageProvider> logger)
    {
        _connectionManager = connectionManager;
        _serializer = serializer;
        _tableManager = tableManager;
        _backplane = backplane;
        _options = options.Value;
        _logger = logger;

        // Build resilience pipeline
        _resilience = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions()
            {
                MaxRetryAttempts = _options.MaxRetryAttempts,
                BackoffType = _options.RetryBackoffType switch
                {
                    SqlServerRetryBackoffType.Linear => DelayBackoffType.Linear,
                    SqlServerRetryBackoffType.Exponential => DelayBackoffType.Exponential,
                    _ => DelayBackoffType.Exponential
                },
                Delay = _options.RetryBaseDelay,
                ShouldHandle = new PredicateBuilder().Handle<SqlException>().Handle<TimeoutException>(),
                OnRetry = args =>
                {
                    _logger.LogWarning("SQL Server storage retry attempt {AttemptNumber}: {Exception}",
                        args.AttemptNumber, args.Outcome.Exception?.Message);
                    return ValueTask.CompletedTask;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions()
            {
                FailureRatio = _options.CircuitBreakerFailureRatio,
                MinimumThroughput = _options.CircuitBreakerMinimumThroughput,
                BreakDuration = _options.CircuitBreakerBreakDuration,
                ShouldHandle = new PredicateBuilder().Handle<SqlException>().Handle<TimeoutException>()
            })
            .AddTimeout(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds))
            .Build();

        // Initialize tables if enabled
        if (_options.EnableAutoTableCreation)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                    await _tableManager.EnsureTablesExistAsync(timeout.Token);
                    _logger.LogInformation("SQL Server cache tables initialized successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initialize SQL Server cache tables");
                }
            });
        }

        // Start background cleanup if enabled
        if (_options.EnableBackgroundCleanup)
        {
            _cleanupTimer = new Timer(PerformCleanup, null, _options.CleanupInterval, _options.CleanupInterval);
        }
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be empty or whitespace.", nameof(key));

        var start = DateTime.UtcNow;
        Interlocked.Increment(ref _getOperations);

        try
        {
            return await _resilience.ExecuteAsync(async _ =>
            {
                await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

                const string sql = @"
                    SELECT [Value], [ExpiresAt]
                    FROM {0}
                    WHERE [Key] = @Key AND ([ExpiresAt] IS NULL OR [ExpiresAt] > GETUTCDATE())";

                var formattedSql = string.Format(sql, _options.FullEntriesTableName);

                await using var command = new SqlCommand(formattedSql, connection)
                {
                    CommandTimeout = _options.CommandTimeoutSeconds
                };
                command.Parameters.AddWithValue("@Key", _options.KeyPrefix + key);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var data = (byte[])reader["Value"];
                    var value = await _serializer.DeserializeAsync<T>(data);

                    if (_options.EnableDetailedLogging)
                    {
                        _logger.LogDebug("Cache hit for key {Key}", key);
                    }

                    return value;
                }

                if (_options.EnableDetailedLogging)
                {
                    _logger.LogDebug("Cache miss for key {Key}", key);
                }

                return default(T);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error getting value for key {Key}", key);
            return default(T);
        }
        finally
        {
            RecordOperationTime(start);
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        await SetAsync(key, value, expiration, Enumerable.Empty<string>(), cancellationToken);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be empty or whitespace.", nameof(key));
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        var start = DateTime.UtcNow;
        Interlocked.Increment(ref _setOperations);

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(_options.IsolationLevel, cancellationToken);

                try
                {
                    var fullKey = _options.KeyPrefix + key;
                    var data = await _serializer.SerializeAsync(value);
                    if (data == null)
                        return; // Skip null values
                    var expiresAt = expiration == TimeSpan.MaxValue ? (DateTime?)null : DateTime.UtcNow.Add(expiration);
                    var tagsArray = tags.ToArray();

                    // Upsert cache entry
                    const string upsertSql = @"
                        MERGE {0} AS target
                        USING (SELECT @Key AS [Key]) AS source ON target.[Key] = source.[Key]
                        WHEN MATCHED THEN
                            UPDATE SET [Value] = @Value, [ExpiresAt] = @ExpiresAt, [UpdatedAt] = GETUTCDATE()
                        WHEN NOT MATCHED THEN
                            INSERT ([Key], [Value], [ExpiresAt], [CreatedAt], [UpdatedAt])
                            VALUES (@Key, @Value, @ExpiresAt, GETUTCDATE(), GETUTCDATE());";

                    var formattedUpsertSql = string.Format(upsertSql, _options.FullEntriesTableName);

                    await using var upsertCommand = new SqlCommand(formattedUpsertSql, connection, (SqlTransaction)transaction)
                    {
                        CommandTimeout = _options.CommandTimeoutSeconds
                    };
                    upsertCommand.Parameters.AddWithValue("@Key", fullKey);
                    upsertCommand.Parameters.AddWithValue("@Value", data);
                    upsertCommand.Parameters.AddWithValue("@ExpiresAt", (object?)expiresAt ?? DBNull.Value);

                    await upsertCommand.ExecuteNonQueryAsync(cancellationToken);

                    // Handle tags if provided
                    if (tagsArray.Length > 0)
                    {
                        // Remove existing tag associations
                        const string deleteTagsSql = "DELETE FROM {0} WHERE [Key] = @Key";
                        var formattedDeleteTagsSql = string.Format(deleteTagsSql, _options.FullTagsTableName);

                        await using var deleteCommand = new SqlCommand(formattedDeleteTagsSql, connection, (SqlTransaction)transaction)
                        {
                            CommandTimeout = _options.CommandTimeoutSeconds
                        };
                        deleteCommand.Parameters.AddWithValue("@Key", fullKey);
                        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

                        // Insert new tag associations
                        const string insertTagSql = "INSERT INTO {0} ([Key], [Tag]) VALUES (@Key, @Tag)";
                        var formattedInsertTagSql = string.Format(insertTagSql, _options.FullTagsTableName);

                        foreach (var tag in tagsArray)
                        {
                            await using var insertCommand = new SqlCommand(formattedInsertTagSql, connection, (SqlTransaction)transaction)
                            {
                                CommandTimeout = _options.CommandTimeoutSeconds
                            };
                            insertCommand.Parameters.AddWithValue("@Key", fullKey);
                            insertCommand.Parameters.AddWithValue("@Tag", tag);
                            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                        }

                        if (_options.EnableDetailedLogging)
                        {
                            _logger.LogDebug("Set key {Key} with {TagCount} tags", key, tagsArray.Length);
                        }
                    }

                    await transaction.CommitAsync(cancellationToken);

                    if (_options.EnableDetailedLogging)
                    {
                        _logger.LogDebug("Set key {Key} with expiration {Expiration}", key, expiration);
                    }
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error setting value for key {Key}", key);
        }
        finally
        {
            RecordOperationTime(start);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be empty or whitespace.", nameof(key));

        var start = DateTime.UtcNow;
        Interlocked.Increment(ref _removeOperations);

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(_options.IsolationLevel, cancellationToken);

                try
                {
                    var fullKey = _options.KeyPrefix + key;

                    // Delete from tags table first (foreign key dependency)
                    const string deleteTagsSql = "DELETE FROM {0} WHERE [Key] = @Key";
                    var formattedDeleteTagsSql = string.Format(deleteTagsSql, _options.FullTagsTableName);

                    await using var deleteTagsCommand = new SqlCommand(formattedDeleteTagsSql, connection, (SqlTransaction)transaction)
                    {
                        CommandTimeout = _options.CommandTimeoutSeconds
                    };
                    deleteTagsCommand.Parameters.AddWithValue("@Key", fullKey);
                    await deleteTagsCommand.ExecuteNonQueryAsync(cancellationToken);

                    // Delete from entries table
                    const string deleteEntrySql = "DELETE FROM {0} WHERE [Key] = @Key";
                    var formattedDeleteEntrySql = string.Format(deleteEntrySql, _options.FullEntriesTableName);

                    await using var deleteEntryCommand = new SqlCommand(formattedDeleteEntrySql, connection, (SqlTransaction)transaction)
                    {
                        CommandTimeout = _options.CommandTimeoutSeconds
                    };
                    deleteEntryCommand.Parameters.AddWithValue("@Key", fullKey);
                    await deleteEntryCommand.ExecuteNonQueryAsync(cancellationToken);

                    await transaction.CommitAsync(cancellationToken);

                    if (_options.EnableDetailedLogging)
                    {
                        _logger.LogDebug("Removed key {Key} and its tag associations", key);
                    }
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error removing key {Key}", key);
        }
        finally
        {
            RecordOperationTime(start);
        }
    }

    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (tag == null)
            throw new ArgumentNullException(nameof(tag));
        if (string.IsNullOrWhiteSpace(tag))
            throw new ArgumentException("Tag cannot be empty or whitespace.", nameof(tag));

        var start = DateTime.UtcNow;

        try
        {
            await _resilience.ExecuteAsync(async _ =>
            {
                await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(_options.IsolationLevel, cancellationToken);

                try
                {
                    // Get keys associated with this tag
                    const string selectKeysSql = "SELECT DISTINCT [Key] FROM {0} WHERE [Tag] = @Tag";
                    var formattedSelectKeysSql = string.Format(selectKeysSql, _options.FullTagsTableName);

                    var keysToDelete = new List<string>();
                    await using var selectCommand = new SqlCommand(formattedSelectKeysSql, connection, (SqlTransaction)transaction)
                    {
                        CommandTimeout = _options.CommandTimeoutSeconds
                    };
                    selectCommand.Parameters.AddWithValue("@Tag", tag);

                    await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        keysToDelete.Add((string)reader["Key"]);
                    }
                    await reader.CloseAsync();

                    if (keysToDelete.Count == 0)
                    {
                        if (_options.EnableDetailedLogging)
                        {
                            _logger.LogDebug("No keys found for tag {Tag}", tag);
                        }
                        await transaction.CommitAsync(cancellationToken);
                        return;
                    }

                    // Delete tag associations first
                    const string deleteTagsSql = "DELETE FROM {0} WHERE [Tag] = @Tag";
                    var formattedDeleteTagsSql = string.Format(deleteTagsSql, _options.FullTagsTableName);

                    await using var deleteTagsCommand = new SqlCommand(formattedDeleteTagsSql, connection, (SqlTransaction)transaction)
                    {
                        CommandTimeout = _options.CommandTimeoutSeconds
                    };
                    deleteTagsCommand.Parameters.AddWithValue("@Tag", tag);
                    await deleteTagsCommand.ExecuteNonQueryAsync(cancellationToken);

                    // Delete entries in batches
                    var batchSize = 1000;
                    for (int i = 0; i < keysToDelete.Count; i += batchSize)
                    {
                        var batch = keysToDelete.Skip(i).Take(batchSize);
                        var keyParameters = string.Join(",", batch.Select((_, index) => $"@Key{index}"));

                        const string deleteEntriesSql = "DELETE FROM {0} WHERE [Key] IN ({1})";
                        var formattedDeleteEntriesSql = string.Format(deleteEntriesSql, _options.FullEntriesTableName, keyParameters);

                        await using var deleteEntriesCommand = new SqlCommand(formattedDeleteEntriesSql, connection, (SqlTransaction)transaction)
                        {
                            CommandTimeout = _options.CommandTimeoutSeconds
                        };

                        var batchList = batch.ToList();
                        for (int j = 0; j < batchList.Count; j++)
                        {
                            deleteEntriesCommand.Parameters.AddWithValue($"@Key{j}", batchList[j]);
                        }

                        await deleteEntriesCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);

                    if (_options.EnableDetailedLogging)
                    {
                        _logger.LogDebug("Removed {KeyCount} keys for tag {Tag}", keysToDelete.Count, tag);
                    }

                    // Publish invalidation event for cross-instance coordination
                    if (_backplane != null)
                    {
                        await _backplane.PublishTagInvalidationAsync(tag, cancellationToken);
                    }
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error removing keys by tag {Tag}", tag);
        }
        finally
        {
            RecordOperationTime(start);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key == null)
            throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Key cannot be empty or whitespace.", nameof(key));

        try
        {
            return await _resilience.ExecuteAsync(async _ =>
            {
                await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

                const string sql = @"
                    SELECT 1 FROM {0}
                    WHERE [Key] = @Key AND ([ExpiresAt] IS NULL OR [ExpiresAt] > GETUTCDATE())";

                var formattedSql = string.Format(sql, _options.FullEntriesTableName);

                await using var command = new SqlCommand(formattedSql, connection)
                {
                    CommandTimeout = _options.CommandTimeoutSeconds
                };
                command.Parameters.AddWithValue("@Key", _options.KeyPrefix + key);

                var result = await command.ExecuteScalarAsync(cancellationToken);
                return result != null;
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            _logger.LogError(ex, "Error checking existence of key {Key}", key);
            return false;
        }
    }

    public async Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            // Simple health check query
            const string sql = "SELECT 1";
            await using var command = new SqlCommand(sql, connection)
            {
                CommandTimeout = 5 // Short timeout for health checks
            };

            var start = DateTime.UtcNow;
            await command.ExecuteScalarAsync(cancellationToken);
            var duration = DateTime.UtcNow - start;

            if (duration.TotalMilliseconds > 1000) // > 1 second is concerning
            {
                _logger.LogWarning("SQL Server health check took {Duration}ms, performance may be degraded", duration.TotalMilliseconds);
                return HealthStatus.Degraded;
            }

            return HealthStatus.Healthy;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQL Server health check failed");
            return HealthStatus.Unhealthy;
        }
    }

    public async Task<PersistentStorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var getOps = Interlocked.Read(ref _getOperations);
            var setOps = Interlocked.Read(ref _setOperations);
            var removeOps = Interlocked.Read(ref _removeOperations);
            var errors = Interlocked.Read(ref _errorCount);

            // Get database statistics
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            const string statsSql = @"
                SELECT
                    COUNT(*) as EntryCount,
                    COUNT(DISTINCT t.[Tag]) as TagCount
                FROM {0} e
                LEFT JOIN {1} t ON e.[Key] = t.[Key]
                WHERE e.[ExpiresAt] IS NULL OR e.[ExpiresAt] > GETUTCDATE()";

            var formattedStatsSql = string.Format(statsSql, _options.FullEntriesTableName, _options.FullTagsTableName);

            await using var command = new SqlCommand(formattedStatsSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };

            long entryCount = 0;
            long tagCount = 0;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                entryCount = reader.IsDBNull("EntryCount") ? 0 : (long)(int)reader["EntryCount"];
                tagCount = reader.IsDBNull("TagCount") ? 0 : (long)(int)reader["TagCount"];
            }

            return new PersistentStorageStats
            {
                GetOperations = getOps,
                SetOperations = setOps,
                RemoveOperations = removeOps,
                ErrorCount = errors,
                AverageResponseTimeMs = CalculateAverageResponseTime(),
                EntryCount = entryCount,
                DiskSpaceUsedBytes = await GetStorageSizeAsync(cancellationToken),
                ExpiredEntriesCleaned = 0, // TODO: Track this
                AveragePersistTimeMs = CalculateAverageResponseTime(),
                AverageRetrievalTimeMs = CalculateAverageResponseTime(),
                ActiveConnections = 1, // Current connection
                LastCleanupTime = DateTime.UtcNow, // TODO: Track actual cleanup time
                AdditionalStats = new Dictionary<string, object>
                {
                    ["ConnectionString"] = MaskConnectionString(_options.ConnectionString),
                    ["Schema"] = _options.Schema,
                    ["EntriesTable"] = _options.EntriesTableName,
                    ["TagsTable"] = _options.TagsTableName,
                    ["KeyPrefix"] = _options.KeyPrefix,
                    ["SerializerType"] = _options.DefaultSerializer.ToString(),
                    ["CommandTimeout"] = _options.CommandTimeoutSeconds,
                    ["RetryAttempts"] = _options.MaxRetryAttempts,
                    ["EntryCount"] = entryCount,
                    ["TagCount"] = tagCount
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting SQL Server storage stats");
            return new PersistentStorageStats
            {
                GetOperations = Interlocked.Read(ref _getOperations),
                SetOperations = Interlocked.Read(ref _setOperations),
                RemoveOperations = Interlocked.Read(ref _removeOperations),
                ErrorCount = Interlocked.Read(ref _errorCount),
                AverageResponseTimeMs = CalculateAverageResponseTime(),
                DiskSpaceUsedBytes = 0,
                EntryCount = 0,
                ExpiredEntriesCleaned = 0,
                AveragePersistTimeMs = 0,
                AverageRetrievalTimeMs = 0,
                ActiveConnections = 0,
                LastCleanupTime = null
            };
        }
    }

    private void RecordOperationTime(DateTime start)
    {
        lock (_statsLock)
        {
            _lastOperationTime = DateTime.UtcNow;
        }
    }

    private double CalculateAverageResponseTime()
    {
        // Simple implementation - could be enhanced with proper sliding window
        lock (_statsLock)
        {
            var timeSinceLastOp = DateTime.UtcNow - _lastOperationTime;
            return Math.Min(timeSinceLastOp.TotalMilliseconds, 5000); // Cap at 5 seconds
        }
    }

    private static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "[not set]";

        // Simple masking for security - hide passwords
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var masked = new List<string>();

        foreach (var part in parts)
        {
            if (part.Trim().StartsWith("Password", StringComparison.OrdinalIgnoreCase) ||
                part.Trim().StartsWith("Pwd", StringComparison.OrdinalIgnoreCase))
            {
                masked.Add("Password=***");
            }
            else
            {
                masked.Add(part);
            }
        }

        return string.Join(";", masked);
    }

    private async void PerformCleanup(object? state)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync();

            const string cleanupSql = @"
                DELETE TOP (@BatchSize) FROM {0}
                WHERE [ExpiresAt] IS NOT NULL AND [ExpiresAt] <= GETUTCDATE()";

            var formattedCleanupSql = string.Format(cleanupSql, _options.FullEntriesTableName);

            await using var command = new SqlCommand(formattedCleanupSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@BatchSize", _options.CleanupBatchSize);

            var deletedCount = await command.ExecuteNonQueryAsync();

            if (deletedCount > 0 && _options.EnableDetailedLogging)
            {
                _logger.LogDebug("Cleaned up {DeletedCount} expired cache entries", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during background cleanup");
        }
    }

    // IPersistentStorageProvider specific methods

    public async Task CleanupExpiredEntriesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            const string deleteSql = @"
                DELETE TOP (@BatchSize)
                FROM {0}
                WHERE [ExpiresAt] IS NOT NULL AND [ExpiresAt] <= GETUTCDATE()";

            var formattedSql = string.Format(deleteSql, _options.FullEntriesTableName);

            await using var command = new SqlCommand(formattedSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@BatchSize", _options.CleanupBatchSize);

            var deletedCount = await command.ExecuteNonQueryAsync(cancellationToken);

            if (deletedCount > 0)
            {
                _logger.LogInformation("L3 cleanup: Removed {DeletedCount} expired entries", deletedCount);
            }

            // Also cleanup orphaned tag entries
            const string cleanupTagsSql = @"
                DELETE FROM {1}
                WHERE NOT EXISTS (
                    SELECT 1 FROM {0} WHERE {0}.[Key] = {1}.[Key]
                )";

            var formattedTagsSql = string.Format(cleanupTagsSql, _options.FullEntriesTableName, _options.FullTagsTableName);
            await using var tagsCommand = new SqlCommand(formattedTagsSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };

            var orphanedTags = await tagsCommand.ExecuteNonQueryAsync(cancellationToken);
            if (orphanedTags > 0)
            {
                _logger.LogDebug("L3 cleanup: Removed {OrphanedTags} orphaned tag entries", orphanedTags);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during L3 persistent storage cleanup");
            throw;
        }
    }

    public async Task<long> GetStorageSizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            const string sizeSql = @"
                SELECT
                    SUM(CAST(DATALENGTH([Value]) AS BIGINT)) as TotalSize
                FROM {0}
                WHERE [ExpiresAt] IS NULL OR [ExpiresAt] > GETUTCDATE()";

            var formattedSql = string.Format(sizeSql, _options.FullEntriesTableName);

            await using var command = new SqlCommand(formattedSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is DBNull || result == null ? 0L : (long)result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting L3 storage size");
            return 0L;
        }
    }

    // Explicit interface implementation for IStorageProvider.GetStatsAsync
    async Task<StorageStats?> IStorageProvider.GetStatsAsync(CancellationToken cancellationToken)
    {
        var persistentStats = await GetStatsAsync(cancellationToken);
        return persistentStats; // PersistentStorageStats inherits from StorageStats
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        try
        {
            _cleanupTimer?.Dispose();
            _logger.LogDebug("SqlServerPersistentStorageProvider disposed");
        }
        finally
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}