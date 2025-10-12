using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.Infrastructure;

/// <summary>
/// SQL Server implementation of IBackplane for distributed cache coordination.
/// Uses polling-based approach to detect and propagate cache invalidation messages.
/// </summary>
public class SqlServerBackplane : IBackplane, IAsyncDisposable
{
    private readonly ISqlServerConnectionManager _connectionManager;
    private readonly SqlServerOptions _options;
    private readonly ILogger<SqlServerBackplane> _logger;
    private readonly Timer? _pollingTimer;
    private readonly Timer? _cleanupTimer;

    private Func<BackplaneMessage, Task>? _messageHandler;
    private long _lastProcessedMessageId = 0;
    private bool _isSubscribed;
    private bool _disposed;

    /// <summary>
    /// Gets the instance ID for this backplane.
    /// </summary>
    public string InstanceId { get; }

    public SqlServerBackplane(
        ISqlServerConnectionManager connectionManager,
        IOptions<SqlServerOptions> options,
        ILogger<SqlServerBackplane> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;

        // Create unique instance identifier
        InstanceId = $"{Environment.MachineName}_{Environment.ProcessId}_{Guid.NewGuid():N}";

        if (_options.EnableBackplane)
        {
            // Start polling timer for checking new invalidation messages
            _pollingTimer = new Timer(PollForMessages, null, _options.BackplanePollingInterval, _options.BackplanePollingInterval);

            // Start cleanup timer for removing old invalidation messages
            _cleanupTimer = new Timer(CleanupOldMessages, null,
                TimeSpan.FromMinutes(10), // Initial delay
                TimeSpan.FromMinutes(10)  // Cleanup every 10 minutes
            );

            _logger.LogInformation("SQL Server backplane initialized with instance ID: {InstanceId}", InstanceId);
        }
    }

    public async Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableBackplane)
        {
            _logger.LogDebug("Backplane is disabled, skipping key invalidation publication");
            return;
        }

        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            const string insertSql = @"
                INSERT INTO {0} ([InstanceId], [MessageType], [TargetKey], [CreatedAt])
                VALUES (@InstanceId, @MessageType, @TargetKey, GETUTCDATE())";

            var formattedSql = string.Format(insertSql, _options.FullInvalidationsTableName);

            await using var command = new SqlCommand(formattedSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@InstanceId", InstanceId);
            command.Parameters.AddWithValue("@MessageType", (byte)BackplaneMessageType.KeyInvalidation);
            command.Parameters.AddWithValue("@TargetKey", key);

            await command.ExecuteNonQueryAsync(cancellationToken);

            if (_options.EnableDetailedLogging)
            {
                _logger.LogDebug("Published key invalidation for: {Key}", key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish key invalidation for: {Key}", key);
        }
    }

    public async Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableBackplane)
        {
            _logger.LogDebug("Backplane is disabled, skipping tag invalidation publication");
            return;
        }

        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync(cancellationToken);

            const string insertSql = @"
                INSERT INTO {0} ([InstanceId], [MessageType], [TargetTag], [CreatedAt])
                VALUES (@InstanceId, @MessageType, @TargetTag, GETUTCDATE())";

            var formattedSql = string.Format(insertSql, _options.FullInvalidationsTableName);

            await using var command = new SqlCommand(formattedSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@InstanceId", InstanceId);
            command.Parameters.AddWithValue("@MessageType", (byte)BackplaneMessageType.TagInvalidation);
            command.Parameters.AddWithValue("@TargetTag", tag);

            await command.ExecuteNonQueryAsync(cancellationToken);

            if (_options.EnableDetailedLogging)
            {
                _logger.LogDebug("Published tag invalidation for: {Tag}", tag);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish tag invalidation for: {Tag}", tag);
        }
    }

    public async Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
    {
        if (!_options.EnableBackplane)
        {
            _logger.LogWarning("Cannot subscribe to backplane messages - backplane is disabled");
            return;
        }

        _messageHandler = onMessage;
        _isSubscribed = true;

        // Reset to 0 to start receiving all messages from this point forward
        _lastProcessedMessageId = 0;

        _logger.LogInformation("Subscribed to SQL Server backplane invalidation messages");

        // Perform an immediate poll to avoid missing messages due to timer delay
        // This ensures any messages published before subscription are processed
        try
        {
            await Task.Run(() => PollForMessages(null), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during initial poll in SubscribeAsync");
        }
    }

    public Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        _messageHandler = null;
        _isSubscribed = false;

        _logger.LogInformation("Unsubscribed from SQL Server backplane invalidation messages");
        return Task.CompletedTask;
    }

    private async void PollForMessages(object? state)
    {
        if (!_isSubscribed || _messageHandler == null || _disposed)
            return;

        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync();

            // Get all messages since last processed ID from other instances
            // ID-based polling is more reliable than timestamp-based
            const string selectSql = @"
                SELECT [Id], [InstanceId], [MessageType], [TargetKey], [TargetTag], [CreatedAt]
                FROM {0}
                WHERE [Id] > @LastProcessedId
                  AND [InstanceId] != @CurrentInstanceId
                ORDER BY [Id]";

            var formattedSql = string.Format(selectSql, _options.FullInvalidationsTableName);

            await using var command = new SqlCommand(formattedSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@LastProcessedId", _lastProcessedMessageId);
            command.Parameters.AddWithValue("@CurrentInstanceId", InstanceId);

            var messages = new List<BackplaneMessage>();

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var messageId = (long)reader["Id"];
                var createdAt = (DateTime)reader["CreatedAt"];

                var message = new BackplaneMessage
                {
                    Type = (BackplaneMessageType)(byte)reader["MessageType"],
                    Key = reader["TargetKey"] is DBNull ? null : (string)reader["TargetKey"],
                    Tag = reader["TargetTag"] is DBNull ? null : (string)reader["TargetTag"],
                    InstanceId = (string)reader["InstanceId"],
                    Timestamp = createdAt
                };

                messages.Add(message);

                // Track the highest message ID we've seen
                if (messageId > _lastProcessedMessageId)
                {
                    _lastProcessedMessageId = messageId;
                }
            }

            // Process messages
            foreach (var message in messages)
            {
                try
                {
                    await _messageHandler(message);

                    if (_options.EnableDetailedLogging)
                    {
                        _logger.LogDebug("Processed backplane message: {Type} - {Key}{Tag}",
                            message.Type, message.Key, message.Tag);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing backplane message: {Type} - {Key}{Tag}",
                        message.Type, message.Key, message.Tag);
                }
            }

            if (messages.Count > 0 && _options.EnableDetailedLogging)
            {
                _logger.LogDebug("Processed {MessageCount} backplane messages", messages.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error polling for backplane messages");
        }
    }

    private async void CleanupOldMessages(object? state)
    {
        if (_disposed)
            return;

        try
        {
            await using var connection = await _connectionManager.GetConnectionAsync();

            // Delete messages older than retention period
            const string deleteSql = @"
                DELETE TOP (1000) FROM {0}
                WHERE [CreatedAt] < @CutoffTime";

            var formattedSql = string.Format(deleteSql, _options.FullInvalidationsTableName);
            var cutoffTime = DateTime.UtcNow.Subtract(_options.BackplaneMessageRetention);

            await using var command = new SqlCommand(formattedSql, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds
            };
            command.Parameters.AddWithValue("@CutoffTime", cutoffTime);

            var deletedCount = await command.ExecuteNonQueryAsync();

            if (deletedCount > 0 && _options.EnableDetailedLogging)
            {
                _logger.LogDebug("Cleaned up {DeletedCount} old backplane messages", deletedCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old backplane messages");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        try
        {
            _pollingTimer?.Dispose();
            _cleanupTimer?.Dispose();
            _messageHandler = null;
            _isSubscribed = false;

            _logger.LogDebug("SQL Server backplane disposed");
        }
        finally
        {
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }
}