namespace MethodCache.Core.Configuration
{
    /// <summary>
    /// Defines how memory usage is calculated for cache statistics.
    /// </summary>
    public enum MemoryUsageCalculationMode
    {
        /// <summary>
        /// Fast estimation using fixed constants. Lowest overhead but least accurate.
        /// Suitable for high-performance scenarios where exact memory usage is not critical.
        /// </summary>
        Fast,

        /// <summary>
        /// Accurate measurement using reflection and serialization. Higher overhead but most accurate.
        /// Suitable for monitoring and debugging scenarios where precise memory usage is important.
        /// </summary>
        Accurate,

        /// <summary>
        /// Sampling-based approach that measures a subset of entries and extrapolates.
        /// Balanced approach between performance and accuracy.
        /// </summary>
        Sampling,

        /// <summary>
        /// Disabled - returns 0 for memory usage. Minimal overhead.
        /// Use when memory usage statistics are not needed.
        /// </summary>
        Disabled
    }
}
