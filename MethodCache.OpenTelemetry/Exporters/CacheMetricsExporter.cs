using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.OpenTelemetry.Metrics;

namespace MethodCache.OpenTelemetry.Exporters;

/// <summary>
/// Represents a cache metrics data point for export
/// </summary>
public class CacheMetricData
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public MetricType Type { get; set; }
    public DateTime Timestamp { get; set; }
    public double Value { get; set; }
    public Dictionary<string, object?> Tags { get; set; } = new();
    public Dictionary<string, object?> Resource { get; set; } = new();
}

/// <summary>
/// Types of metrics
/// </summary>
public enum MetricType
{
    Counter,
    Histogram,
    Gauge
}

/// <summary>
/// Interface for exporters that handle cache metrics
/// </summary>
public interface ICacheMetricsExporter : ICacheExporter<CacheMetricData>
{
    /// <summary>
    /// Gets the supported metric types
    /// </summary>
    IReadOnlySet<MetricType> SupportedTypes { get; }
}

/// <summary>
/// Abstract base class for cache metrics exporters
/// </summary>
public abstract class CacheMetricsExporterBase : CacheExporterBase<CacheMetricData>, ICacheMetricsExporter
{
    protected CacheMetricsExporterBase(CacheExporterOptions options) : base(options)
    {
    }

    public abstract IReadOnlySet<MetricType> SupportedTypes { get; }

    protected virtual bool ShouldExport(CacheMetricData metric)
    {
        return SupportedTypes.Contains(metric.Type);
    }
}

/// <summary>
/// Custom metrics exporter that allows users to implement their own export logic
/// </summary>
public class CustomMetricsExporter : CacheMetricsExporterBase
{
    private readonly Func<IEnumerable<CacheMetricData>, CancellationToken, Task<ExportResult>> _exportFunc;
    private readonly IReadOnlySet<MetricType> _supportedTypes;

    public CustomMetricsExporter(
        string name,
        Func<IEnumerable<CacheMetricData>, CancellationToken, Task<ExportResult>> exportFunc,
        CacheExporterOptions options,
        IReadOnlySet<MetricType>? supportedTypes = null)
        : base(options)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _exportFunc = exportFunc ?? throw new ArgumentNullException(nameof(exportFunc));
        _supportedTypes = supportedTypes ?? new HashSet<MetricType> { MetricType.Counter, MetricType.Histogram, MetricType.Gauge };
    }

    public override string Name { get; }

    public override IReadOnlySet<MetricType> SupportedTypes => _supportedTypes;

    public override async Task<ExportResult> ExportAsync(IEnumerable<CacheMetricData> batch, CancellationToken cancellationToken = default)
    {
        ThrowIfShutdown();

        try
        {
            var filteredBatch = new List<CacheMetricData>();
            foreach (var metric in batch)
            {
                if (ShouldExport(metric))
                {
                    filteredBatch.Add(metric);
                }
            }

            if (filteredBatch.Count == 0)
            {
                return ExportResult.Success;
            }

            return await _exportFunc(filteredBatch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExportResult.Failure;
        }
        catch (Exception)
        {
            return ExportResult.Failure;
        }
    }
}

/// <summary>
/// Factory for creating cache metrics exporters
/// </summary>
public interface ICacheMetricsExporterFactory
{
    /// <summary>
    /// Creates a metrics exporter by name
    /// </summary>
    ICacheMetricsExporter CreateExporter(string name, CacheExporterOptions options);

    /// <summary>
    /// Registers a custom exporter factory function
    /// </summary>
    void RegisterExporter(string name, Func<CacheExporterOptions, ICacheMetricsExporter> factory);

    /// <summary>
    /// Gets all registered exporter names
    /// </summary>
    IEnumerable<string> GetAvailableExporters();
}

/// <summary>
/// Default implementation of metrics exporter factory
/// </summary>
public class CacheMetricsExporterFactory : ICacheMetricsExporterFactory
{
    private readonly Dictionary<string, Func<CacheExporterOptions, ICacheMetricsExporter>> _factories = new();

    public ICacheMetricsExporter CreateExporter(string name, CacheExporterOptions options)
    {
        if (!_factories.TryGetValue(name, out var factory))
        {
            throw new InvalidOperationException($"No exporter factory registered for name: {name}");
        }

        return factory(options);
    }

    public void RegisterExporter(string name, Func<CacheExporterOptions, ICacheMetricsExporter> factory)
    {
        _factories[name] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public IEnumerable<string> GetAvailableExporters()
    {
        return _factories.Keys;
    }
}