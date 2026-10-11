using System.Diagnostics.Metrics;

namespace MethodCache.OpenTelemetry;

public static class MetricInstruments
{
    public const string MeterName = "MethodCache";
    public const string MeterVersion = "1.0.0";

    public static class Names
    {
        public const string HitsTotal = "methodcache.hits_total";
        public const string MissesTotal = "methodcache.misses_total";
        public const string ErrorsTotal = "methodcache.errors_total";
        public const string OperationDuration = "methodcache.operation_duration";
        public const string LoadDuration = "methodcache.load_duration";
        public const string KeyGenerationDuration = "methodcache.key_generation_duration";
        public const string EntriesCount = "methodcache.entries_count";
        public const string MemoryBytes = "methodcache.memory_bytes";
        public const string HitRatio = "methodcache.hit_ratio";
    }

    public static class Units
    {
        public const string Count = "{count}";
        public const string Milliseconds = "ms";
        public const string Bytes = "By";
        public const string Ratio = "{ratio}";
    }

    public static class Descriptions
    {
        public const string HitsTotal = "Total number of cache hits";
        public const string MissesTotal = "Total number of cache misses";
        public const string ErrorsTotal = "Total number of cache errors";
        public const string OperationDuration = "Duration of cache operations";
        public const string LoadDuration = "Time the cached method took to produce a value after a miss or during a refresh";
        public const string KeyGenerationDuration = "Duration of cache key generation";
        public const string EntriesCount = "Current number of entries in cache";
        public const string MemoryBytes = "Current memory usage in bytes";
        public const string HitRatio = "Cache hit ratio";
    }
}