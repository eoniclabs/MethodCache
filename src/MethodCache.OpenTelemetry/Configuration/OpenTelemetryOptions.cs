namespace MethodCache.OpenTelemetry;

/// <summary>Options for <c>AddMethodCacheOpenTelemetry</c>.</summary>
public class OpenTelemetryOptions
{
    /// <summary>Creates a span for each cache operation. Default: true.</summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>Records the MethodCache metrics. Default: true.</summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>Records the cache key on spans. Default: false, since keys can contain argument values.</summary>
    public bool RecordCacheKeys { get; set; } = false;

    /// <summary>
    /// Hash cache keys when recording them. Only applies when RecordCacheKeys is true.
    /// </summary>
    public bool HashCacheKeys { get; set; } = true;

    /// <summary>
    /// Records the cache tags and invalidation patterns on spans. Default: false, since a tag built from a template
    /// such as <c>user:{userId}</c> contains the argument value.
    /// </summary>
    public bool RecordCacheTags { get; set; } = false;

    /// <summary>
    /// Puts the user and tenant ids set through <see cref="IBaggagePropagator"/> into span baggage,
    /// which travels to downstream services. Default: false.
    /// </summary>
    public bool ExportSensitiveData { get; set; } = false;

    /// <summary>Copies the request's <c>http.*</c> and <c>url.*</c> attributes onto cache spans. Default: true.</summary>
    public bool EnableHttpCorrelation { get; set; } = true;

    /// <summary>Carries <c>cache.*</c> baggage between spans. Default: true.</summary>
    public bool EnableBaggagePropagation { get; set; } = true;
}
