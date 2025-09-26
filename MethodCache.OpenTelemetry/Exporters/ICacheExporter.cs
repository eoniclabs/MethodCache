using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.OpenTelemetry.Exporters;

/// <summary>
/// Defines a contract for exporting cache telemetry data to external systems
/// </summary>
/// <typeparam name="T">The type of telemetry data to export</typeparam>
public interface ICacheExporter<in T> : IDisposable
{
    /// <summary>
    /// Gets the name of this exporter
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Exports a batch of telemetry data
    /// </summary>
    Task<ExportResult> ExportAsync(IEnumerable<T> batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces any pending exports to complete
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Shuts down the exporter gracefully
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the result of an export operation
/// </summary>
public enum ExportResult
{
    /// <summary>
    /// The export was successful
    /// </summary>
    Success,

    /// <summary>
    /// The export failed but can be retried
    /// </summary>
    Failure,

    /// <summary>
    /// The export failed and should not be retried
    /// </summary>
    FailureNotRetryable
}

/// <summary>
/// Base class for cache exporters with common functionality
/// </summary>
/// <typeparam name="T">The type of telemetry data to export</typeparam>
public abstract class CacheExporterBase<T> : ICacheExporter<T>
{
    protected readonly CacheExporterOptions Options;
    protected volatile bool IsShutdown;

    protected CacheExporterBase(CacheExporterOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public abstract string Name { get; }

    public abstract Task<ExportResult> ExportAsync(IEnumerable<T> batch, CancellationToken cancellationToken = default);

    public virtual Task FlushAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public virtual Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        IsShutdown = true;
        return Task.CompletedTask;
    }

    protected virtual void ThrowIfShutdown()
    {
        if (IsShutdown)
        {
            throw new ObjectDisposedException(GetType().Name);
        }
    }

    public virtual void Dispose()
    {
        if (!IsShutdown)
        {
            ShutdownAsync().GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// Configuration options for cache exporters
/// </summary>
public class CacheExporterOptions
{
    /// <summary>
    /// Maximum batch size for export operations
    /// </summary>
    public int MaxBatchSize { get; set; } = 512;

    /// <summary>
    /// Export timeout in milliseconds
    /// </summary>
    public int ExportTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Maximum number of retry attempts
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Retry delay in milliseconds
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Whether to compress exported data
    /// </summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>
    /// Custom headers to include with exports
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Additional configuration properties
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}