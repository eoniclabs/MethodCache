namespace MethodCache;

/// <summary>Identifies the Core meter and activity source for cache instrumentation.</summary>
public static class MethodCacheTelemetry
{
    /// <summary>The name used by both the meter and activity source.</summary>
    public const string Name = "MethodCache";
}

/// <summary>Controls optional cache identities on spans. Metrics never contain cache keys or tags.</summary>
public sealed class MethodCacheTelemetryOptions
{
    /// <summary>Records keys on load and invalidation spans. Default false.</summary>
    public bool RecordCacheKeys { get; set; }

    /// <summary>Hashes recorded keys with SHA-256. Default true; applies only when RecordCacheKeys is enabled.</summary>
    public bool HashCacheKeys { get; set; } = true;

    /// <summary>Records application tags on spans. Default false.</summary>
    public bool RecordCacheTags { get; set; }
}
