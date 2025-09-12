namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Defines how memory usage is calculated for cache statistics.
    /// 
    /// WARNING: All modes provide approximations only. Actual .NET memory usage
    /// may vary significantly due to runtime optimizations, GC behavior, and
    /// object layout complexities. Use for trends and relative comparisons only.
    /// </summary>
    public enum MemoryUsageCalculationMode
    {
        /// <summary>
        /// Fast estimation using fixed constants and type-based guesses.
        /// Lowest overhead but highly inaccurate for absolute memory usage.
        /// Results may be off by orders of magnitude from actual memory usage.
        /// Suitable only for performance monitoring trends, not capacity planning.
        /// </summary>
        Fast,

        /// <summary>
        /// "Accurate" measurement using JSON serialization (MISLEADING NAME).
        /// Higher overhead but NOT significantly more accurate than Fast mode.
        /// JSON serialization size bears little resemblance to actual memory footprint.
        /// The name "Accurate" is misleading - this is still an approximation.
        /// Consider using Fast mode instead as it's cheaper with similar accuracy.
        /// </summary>
        Accurate,

        /// <summary>
        /// Sampling-based approach using JSON serialization on a subset of entries.
        /// Inherits the accuracy limitations of Accurate mode but adds statistical variance.
        /// Not recommended - provides neither good performance nor meaningful accuracy.
        /// Consider Fast mode for performance or Disabled mode if statistics aren't needed.
        /// </summary>
        Sampling,

        /// <summary>
        /// Disabled - returns 0 for memory usage. Minimal overhead.
        /// Use when memory usage statistics are not needed.
        /// </summary>
        Disabled
    }
}
