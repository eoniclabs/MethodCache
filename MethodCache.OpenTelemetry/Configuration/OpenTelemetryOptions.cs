using System;

namespace MethodCache.OpenTelemetry.Configuration;

public class OpenTelemetryOptions
{
    public bool EnableTracing { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool RecordCacheKeys { get; set; } = false;
    /// <summary>
    /// Hash cache keys when recording them. Only applies when RecordCacheKeys is true.
    /// </summary>
    public bool HashCacheKeys { get; set; } = true;
    public double SamplingRatio { get; set; } = 1.0;
    public bool ExportSensitiveData { get; set; } = false;
    public TimeSpan MetricExportInterval { get; set; } = TimeSpan.FromSeconds(60);
    public bool EnableHttpCorrelation { get; set; } = true;
    public bool EnableBaggagePropagation { get; set; } = true;
    public bool EnableDistributedTracing { get; set; } = true;
    public bool EnableStorageProviderInstrumentation { get; set; } = true;
    public string? ServiceName { get; set; }
    public string? ServiceVersion { get; set; }
    public string? ServiceNamespace { get; set; }
    public string? Environment { get; set; }

    public void Validate()
    {
        if (SamplingRatio < 0 || SamplingRatio > 1)
        {
            throw new ArgumentException("SamplingRatio must be between 0 and 1", nameof(SamplingRatio));
        }

        if (MetricExportInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("MetricExportInterval must be at least 1 second", nameof(MetricExportInterval));
        }

        if (HashCacheKeys && !RecordCacheKeys)
        {
            throw new ArgumentException("HashCacheKeys can only be true when RecordCacheKeys is true", nameof(HashCacheKeys));
        }
    }
}