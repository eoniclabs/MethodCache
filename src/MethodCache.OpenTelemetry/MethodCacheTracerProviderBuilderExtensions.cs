using System;
using MethodCache.OpenTelemetry;

namespace OpenTelemetry.Trace;

/// <summary>Adds MethodCache's spans to an OpenTelemetry tracer provider.</summary>
public static class MethodCacheTracerProviderBuilderExtensions
{
    /// <summary>Listens to MethodCache's activity source.</summary>
    public static TracerProviderBuilder AddMethodCacheInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(TracingConstants.ActivitySourceName);
    }
}
