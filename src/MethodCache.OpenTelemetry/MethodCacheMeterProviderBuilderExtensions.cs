using System;
using MethodCache.OpenTelemetry;

namespace OpenTelemetry.Metrics;

/// <summary>Adds MethodCache's metrics to an OpenTelemetry meter provider.</summary>
public static class MethodCacheMeterProviderBuilderExtensions
{
    /// <summary>Listens to MethodCache's meter.</summary>
    public static MeterProviderBuilder AddMethodCacheInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(MetricInstruments.MeterName);
    }
}
