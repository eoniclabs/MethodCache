using System.Diagnostics.Metrics;

namespace MethodCache.OpenTelemetry;

/// <summary>
/// Names, units and descriptions of the metrics MethodCache records.
/// </summary>
public static class MetricInstruments
{
    /// <summary>
    /// The name of the <see cref="System.Diagnostics.Metrics.Meter"/> MethodCache records its metrics with.
    /// </summary>
    public const string MeterName = "MethodCache";

    /// <summary>
    /// The version of the meter.
    /// </summary>
    public const string MeterVersion = "1.0.0";

    /// <summary>
    /// Instrument names.
    /// </summary>
    public static class Names
    {
        /// <summary>
        /// Cache hits, per method.
        /// </summary>
        public const string HitsTotal = "methodcache.hits_total";

        /// <summary>
        /// Cache misses, per method.
        /// </summary>
        public const string MissesTotal = "methodcache.misses_total";

        /// <summary>
        /// Errors, per method.
        /// </summary>
        public const string ErrorsTotal = "methodcache.errors_total";

        /// <summary>
        /// How long cache operations take.
        /// </summary>
        public const string OperationDuration = "methodcache.operation_duration";

        /// <summary>
        /// How long the cached method took to produce a value after a miss or during a refresh.
        /// </summary>
        public const string LoadDuration = "methodcache.load_duration";

        /// <summary>
        /// How long building cache keys takes.
        /// </summary>
        public const string KeyGenerationDuration = "methodcache.key_generation_duration";

        /// <summary>
        /// Entries in the in-memory cache.
        /// </summary>
        public const string EntriesCount = "methodcache.entries_count";

        /// <summary>
        /// Estimated memory used by the in-memory cache.
        /// </summary>
        public const string MemoryBytes = "methodcache.memory_bytes";

        /// <summary>
        /// Hits divided by hits plus misses.
        /// </summary>
        public const string HitRatio = "methodcache.hit_ratio";
    }

    /// <summary>
    /// Instrument units.
    /// </summary>
    public static class Units
    {
        /// <summary>
        /// A count.
        /// </summary>
        public const string Count = "{count}";

        /// <summary>
        /// Milliseconds.
        /// </summary>
        public const string Milliseconds = "ms";

        /// <summary>
        /// Bytes.
        /// </summary>
        public const string Bytes = "By";

        /// <summary>
        /// A ratio between 0 and 1.
        /// </summary>
        public const string Ratio = "{ratio}";
    }

    /// <summary>
    /// Instrument descriptions.
    /// </summary>
    public static class Descriptions
    {
        /// <summary>
        /// The description of the HitsTotal instrument.
        /// </summary>
        public const string HitsTotal = "Total number of cache hits";

        /// <summary>
        /// The description of the MissesTotal instrument.
        /// </summary>
        public const string MissesTotal = "Total number of cache misses";

        /// <summary>
        /// The description of the ErrorsTotal instrument.
        /// </summary>
        public const string ErrorsTotal = "Total number of cache errors";

        /// <summary>
        /// The description of the OperationDuration instrument.
        /// </summary>
        public const string OperationDuration = "Duration of cache operations";

        /// <summary>
        /// The description of the LoadDuration instrument.
        /// </summary>
        public const string LoadDuration = "Time the cached method took to produce a value after a miss or during a refresh";

        /// <summary>
        /// The description of the KeyGenerationDuration instrument.
        /// </summary>
        public const string KeyGenerationDuration = "Duration of cache key generation";

        /// <summary>
        /// The description of the EntriesCount instrument.
        /// </summary>
        public const string EntriesCount = "Current number of entries in cache";

        /// <summary>
        /// The description of the MemoryBytes instrument.
        /// </summary>
        public const string MemoryBytes = "Current memory usage in bytes";

        /// <summary>
        /// The description of the HitRatio instrument.
        /// </summary>
        public const string HitRatio = "Cache hit ratio";
    }
}