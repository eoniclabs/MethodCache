using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.OpenTelemetry.Correlation;

/// <summary>
/// Types of operations that can be correlated
/// </summary>
public enum CorrelationType
{
    HttpRequest,
    DatabaseQuery,
    MessageQueue,
    BackgroundJob,
    DistributedLock,
    CacheOperation,
    ExternalService,
    Custom
}

/// <summary>
/// Correlation context information
/// </summary>
public class CorrelationContext
{
    public string CorrelationId { get; set; } = string.Empty;
    public string? ParentCorrelationId { get; set; }
    public CorrelationType Type { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public string? UserId { get; set; }
    public string? TenantId { get; set; }
    public string? SessionId { get; set; }
    public string? RequestId { get; set; }
    public Dictionary<string, object?> Properties { get; set; } = new();
    public List<string> Tags { get; set; } = new();
}

/// <summary>
/// Advanced correlation tracking options
/// </summary>
public class AdvancedCorrelationOptions
{
    /// <summary>
    /// Enable automatic correlation for database queries
    /// </summary>
    public bool EnableDatabaseCorrelation { get; set; } = true;

    /// <summary>
    /// Enable automatic correlation for message queue operations
    /// </summary>
    public bool EnableMessageQueueCorrelation { get; set; } = true;

    /// <summary>
    /// Enable automatic correlation for background jobs
    /// </summary>
    public bool EnableBackgroundJobCorrelation { get; set; } = true;

    /// <summary>
    /// Enable correlation for distributed lock operations
    /// </summary>
    public bool EnableDistributedLockCorrelation { get; set; } = true;

    /// <summary>
    /// Enable correlation for external service calls
    /// </summary>
    public bool EnableExternalServiceCorrelation { get; set; } = true;

    /// <summary>
    /// Maximum number of correlation contexts to track
    /// </summary>
    public int MaxActiveCorrelations { get; set; } = 10000;

    /// <summary>
    /// How long to keep correlation contexts
    /// </summary>
    public TimeSpan CorrelationTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Enable correlation cleanup background task
    /// </summary>
    public bool EnableCleanup { get; set; } = true;

    /// <summary>
    /// Cleanup interval
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Interface for advanced correlation management
/// </summary>
public interface IAdvancedCorrelationManager
{
    /// <summary>
    /// Starts a new correlation context
    /// </summary>
    CorrelationContext StartCorrelation(CorrelationType type, string operationName, string? parentId = null);

    /// <summary>
    /// Ends a correlation context
    /// </summary>
    void EndCorrelation(string correlationId);

    /// <summary>
    /// Gets the current correlation context
    /// </summary>
    CorrelationContext? GetCurrentCorrelation();

    /// <summary>
    /// Gets a correlation context by ID
    /// </summary>
    CorrelationContext? GetCorrelation(string correlationId);

    /// <summary>
    /// Sets a property on the current correlation context
    /// </summary>
    void SetProperty(string key, object? value);

    /// <summary>
    /// Adds a tag to the current correlation context
    /// </summary>
    void AddTag(string tag);

    /// <summary>
    /// Gets all active correlations
    /// </summary>
    IEnumerable<CorrelationContext> GetActiveCorrelations();

    /// <summary>
    /// Correlates cache operations with database queries
    /// </summary>
    void CorrelateCacheWithDatabase(string cacheKey, string query, string? connectionString = null);

    /// <summary>
    /// Correlates cache operations with message queue operations
    /// </summary>
    void CorrelateCacheWithMessageQueue(string cacheKey, string queueName, string messageId);

    /// <summary>
    /// Correlates cache operations with external service calls
    /// </summary>
    void CorrelateCacheWithExternalService(string cacheKey, string serviceName, string endpoint);
}

/// <summary>
/// Advanced correlation manager implementation
/// </summary>
public class AdvancedCorrelationManager : IAdvancedCorrelationManager, IDisposable
{
    private readonly AdvancedCorrelationOptions _options;
    private readonly ILogger<AdvancedCorrelationManager> _logger;
    private readonly ConcurrentDictionary<string, CorrelationContext> _activeCorrelations = new();
    private readonly AsyncLocal<CorrelationContext?> _currentCorrelation = new();
    private readonly Timer? _cleanupTimer;
    private volatile bool _disposed;

    public AdvancedCorrelationManager(IOptions<AdvancedCorrelationOptions> options, ILogger<AdvancedCorrelationManager> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (_options.EnableCleanup)
        {
            _cleanupTimer = new Timer(CleanupExpiredCorrelations, null, _options.CleanupInterval, _options.CleanupInterval);
        }
    }

    public CorrelationContext StartCorrelation(CorrelationType type, string operationName, string? parentId = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedCorrelationManager));

        var correlationId = GenerateCorrelationId();
        var context = new CorrelationContext
        {
            CorrelationId = correlationId,
            ParentCorrelationId = parentId ?? _currentCorrelation.Value?.CorrelationId,
            Type = type,
            OperationName = operationName,
            StartTime = DateTime.UtcNow
        };

        // Copy properties from current correlation
        if (_currentCorrelation.Value != null)
        {
            context.UserId = _currentCorrelation.Value.UserId;
            context.TenantId = _currentCorrelation.Value.TenantId;
            context.SessionId = _currentCorrelation.Value.SessionId;
            context.RequestId = _currentCorrelation.Value.RequestId;
        }

        // Check capacity
        if (_activeCorrelations.Count >= _options.MaxActiveCorrelations)
        {
            _logger.LogWarning("Maximum correlation capacity reached. Forcing cleanup.");
            CleanupExpiredCorrelations(null);
        }

        _activeCorrelations[correlationId] = context;
        _currentCorrelation.Value = context;

        // Set Activity tags for OpenTelemetry integration
        var activity = Activity.Current;
        if (activity != null)
        {
            activity.SetTag("correlation.id", correlationId);
            activity.SetTag("correlation.type", type.ToString());
            activity.SetTag("correlation.operation", operationName);

            if (!string.IsNullOrEmpty(parentId))
            {
                activity.SetTag("correlation.parent_id", parentId);
            }
        }

        _logger.LogDebug("Started correlation {CorrelationId} of type {Type} for operation {Operation}",
            correlationId, type, operationName);

        return context;
    }

    public void EndCorrelation(string correlationId)
    {
        if (_activeCorrelations.TryRemove(correlationId, out var context))
        {
            var duration = DateTime.UtcNow - context.StartTime;

            _logger.LogDebug("Ended correlation {CorrelationId} after {Duration}ms",
                correlationId, duration.TotalMilliseconds);

            // Clear current correlation if it matches
            if (_currentCorrelation.Value?.CorrelationId == correlationId)
            {
                _currentCorrelation.Value = null;
            }

            // Set Activity tags
            var activity = Activity.Current;
            if (activity != null)
            {
                activity.SetTag("correlation.duration_ms", duration.TotalMilliseconds);
                activity.SetTag("correlation.ended", true);
            }
        }
    }

    public CorrelationContext? GetCurrentCorrelation()
    {
        return _currentCorrelation.Value;
    }

    public CorrelationContext? GetCorrelation(string correlationId)
    {
        return _activeCorrelations.TryGetValue(correlationId, out var context) ? context : null;
    }

    public void SetProperty(string key, object? value)
    {
        var current = _currentCorrelation.Value;
        if (current != null)
        {
            current.Properties[key] = value;

            // Also set as Activity tag if current
            var activity = Activity.Current;
            if (activity != null)
            {
                activity.SetTag($"correlation.{key}", value?.ToString());
            }
        }
    }

    public void AddTag(string tag)
    {
        var current = _currentCorrelation.Value;
        if (current != null)
        {
            current.Tags.Add(tag);

            // Also set as Activity tag
            var activity = Activity.Current;
            if (activity != null)
            {
                var existingTags = activity.GetTagItem("correlation.tags")?.ToString() ?? "";
                var newTags = string.IsNullOrEmpty(existingTags) ? tag : $"{existingTags},{tag}";
                activity.SetTag("correlation.tags", newTags);
            }
        }
    }

    public IEnumerable<CorrelationContext> GetActiveCorrelations()
    {
        return _activeCorrelations.Values;
    }

    public void CorrelateCacheWithDatabase(string cacheKey, string query, string? connectionString = null)
    {
        if (!_options.EnableDatabaseCorrelation) return;

        var current = _currentCorrelation.Value;
        if (current != null)
        {
            SetProperty("db.query", SanitizeQuery(query));
            SetProperty("db.connection_string", SanitizeConnectionString(connectionString));
            SetProperty("cache.key", cacheKey);
            AddTag("cache-db-correlation");

            _logger.LogDebug("Correlated cache key {CacheKey} with database query in correlation {CorrelationId}",
                cacheKey, current.CorrelationId);
        }
    }

    public void CorrelateCacheWithMessageQueue(string cacheKey, string queueName, string messageId)
    {
        if (!_options.EnableMessageQueueCorrelation) return;

        var current = _currentCorrelation.Value;
        if (current != null)
        {
            SetProperty("mq.queue", queueName);
            SetProperty("mq.message_id", messageId);
            SetProperty("cache.key", cacheKey);
            AddTag("cache-mq-correlation");

            _logger.LogDebug("Correlated cache key {CacheKey} with message queue {QueueName} in correlation {CorrelationId}",
                cacheKey, queueName, current.CorrelationId);
        }
    }

    public void CorrelateCacheWithExternalService(string cacheKey, string serviceName, string endpoint)
    {
        if (!_options.EnableExternalServiceCorrelation) return;

        var current = _currentCorrelation.Value;
        if (current != null)
        {
            SetProperty("external.service", serviceName);
            SetProperty("external.endpoint", SanitizeEndpoint(endpoint));
            SetProperty("cache.key", cacheKey);
            AddTag("cache-external-correlation");

            _logger.LogDebug("Correlated cache key {CacheKey} with external service {ServiceName} in correlation {CorrelationId}",
                cacheKey, serviceName, current.CorrelationId);
        }
    }

    private void CleanupExpiredCorrelations(object? state)
    {
        if (_disposed) return;

        try
        {
            var cutoff = DateTime.UtcNow - _options.CorrelationTimeout;
            var expiredKeys = new List<string>();

            foreach (var kvp in _activeCorrelations)
            {
                if (kvp.Value.StartTime < cutoff)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            foreach (var key in expiredKeys)
            {
                if (_activeCorrelations.TryRemove(key, out var expired))
                {
                    _logger.LogDebug("Cleaned up expired correlation {CorrelationId} of type {Type}",
                        expired.CorrelationId, expired.Type);
                }
            }

            if (expiredKeys.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired correlations", expiredKeys.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during correlation cleanup");
        }
    }

    private static string GenerateCorrelationId()
    {
        return Guid.NewGuid().ToString("N")[..16];
    }

    private static string SanitizeQuery(string query)
    {
        // Remove potential PII from SQL queries
        if (string.IsNullOrEmpty(query)) return query;

        // Simple sanitization - remove string literals
        return System.Text.RegularExpressions.Regex.Replace(query, @"'[^']*'", "'<redacted>'");
    }

    private static string? SanitizeConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString)) return connectionString;

        // Remove passwords and sensitive info from connection strings
        return System.Text.RegularExpressions.Regex.Replace(connectionString,
            @"(password|pwd|secret|key)\s*=\s*[^;]+",
            "$1=<redacted>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string SanitizeEndpoint(string endpoint)
    {
        if (string.IsNullOrEmpty(endpoint)) return endpoint;

        try
        {
            var uri = new Uri(endpoint);
            return $"{uri.Scheme}://{uri.Host}{uri.PathAndQuery}";
        }
        catch
        {
            return "<invalid-endpoint>";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer?.Dispose();
        _activeCorrelations.Clear();

        _logger.LogInformation("Advanced correlation manager disposed");
    }
}