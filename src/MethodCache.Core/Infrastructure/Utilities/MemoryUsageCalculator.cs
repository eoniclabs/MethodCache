using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace MethodCache.Core.Infrastructure.Utilities
{
    /// <summary>
    /// Calculates memory usage for cache entries using different strategies.
    /// 
    /// WARNING: All calculation modes provide approximations only. Actual memory usage
    /// may vary significantly due to:
    /// - .NET runtime memory allocation overhead
    /// - Object reference tracking
    /// - Garbage collector behavior
    /// - Memory padding and alignment
    /// - String interning and other optimizations
    /// 
    /// These calculations should be used for relative comparison and trends,
    /// not absolute memory usage decisions.
    /// </summary>
    internal sealed class MemoryUsageCalculator
    {
        private readonly MethodCacheMemoryOptions _options;
        private long _operationCounter = 0;
        private long _lastAccurateCalculation = 0;
        private readonly Random _random = new();

        public MemoryUsageCalculator(MethodCacheMemoryOptions options)
        {
            _options = options;
        }

        /// <summary>
        /// Calculates memory usage for the entire cache.
        /// </summary>
        public long CalculateMemoryUsage<T>(ConcurrentDictionary<string, T> cache, Func<T, object> valueExtractor)
        {
            return _options.MemoryCalculationMode switch
            {
                MemoryUsageCalculationMode.Fast => CalculateFast(cache, valueExtractor),
                MemoryUsageCalculationMode.Accurate => CalculateAccurate(cache, valueExtractor),
                MemoryUsageCalculationMode.Sampling => CalculateSampling(cache, valueExtractor),
                MemoryUsageCalculationMode.Disabled => 0,
                _ => CalculateFast(cache, valueExtractor)
            };
        }

        /// <summary>
        /// Fast estimation using fixed constants and type-based estimates.
        /// 
        /// WARNING: This mode uses hardcoded overhead values and rough type-based
        /// size estimates. Results may be significantly different from actual memory usage.
        /// Use only for relative comparisons and performance monitoring trends.
        /// </summary>
        private long CalculateFast<T>(ConcurrentDictionary<string, T> cache, Func<T, object> valueExtractor)
        {
            if (cache.Count == 0) return 0;

            const long overheadPerEntry = 120; // ConcurrentDictionary overhead + EnhancedCacheEntry overhead
            const long averageKeySize = 50;
            
            // Improved value size estimation based on common types
            long averageValueSize = 500; // Default fallback
            
            // Sample a few entries to get better type-based estimates
            var sampleSize = Math.Min(10, cache.Count);
            var samples = cache.Take(sampleSize).ToList();
            
            if (samples.Any())
            {
                var totalEstimatedSize = 0L;
                foreach (var kvp in samples)
                {
                    var valueSize = EstimateValueSizeFast(valueExtractor(kvp.Value));
                    totalEstimatedSize += valueSize;
                }
                averageValueSize = totalEstimatedSize / samples.Count;
            }

            return cache.Count * (overheadPerEntry + averageKeySize + averageValueSize);
        }

        /// <summary>
        /// "Accurate" measurement using JSON serialization (with caching and throttling).
        /// 
        /// WARNING: This uses JSON serialization size as a proxy for memory usage,
        /// which is fundamentally flawed. JSON is a text representation and bears
        /// little resemblance to actual object memory footprint in .NET.
        /// 
        /// This mode is more expensive than Fast mode but not significantly more accurate
        /// for actual memory usage estimation.
        /// </summary>
        private long CalculateAccurate<T>(ConcurrentDictionary<string, T> cache, Func<T, object> valueExtractor)
        {
            var currentOperation = Interlocked.Increment(ref _operationCounter);
            
            // Only recalculate if enough operations have passed
            if (currentOperation - _lastAccurateCalculation < _options.AccurateModeRecalculationInterval)
            {
                // Return fast calculation for intermediate calls
                return CalculateFast(cache, valueExtractor);
            }

            Interlocked.Exchange(ref _lastAccurateCalculation, currentOperation);

            if (cache.Count == 0) return 0;

            long totalSize = 0;
            const long overheadPerEntry = 120; // ConcurrentDictionary + EnhancedCacheEntry overhead

            foreach (var kvp in cache)
            {
                var keySize = Encoding.UTF8.GetByteCount(kvp.Key);
                var valueSize = CalculateValueSizeAccurate(valueExtractor(kvp.Value));
                totalSize += overheadPerEntry + keySize + valueSize;
            }

            return totalSize;
        }

        /// <summary>
        /// Sampling-based calculation for balance between performance and accuracy.
        /// 
        /// WARNING: Inherits the same accuracy limitations as the "Accurate" mode
        /// but applies them to a sample subset. The sampling adds statistical variance
        /// on top of the fundamental measurement inaccuracies.
        /// </summary>
        private long CalculateSampling<T>(ConcurrentDictionary<string, T> cache, Func<T, object> valueExtractor)
        {
            if (cache.Count == 0) return 0;

            var sampleSize = Math.Max(1, (int)(cache.Count * _options.SamplingPercentage));
            var samples = cache.OrderBy(x => _random.Next()).Take(sampleSize).ToList();

            if (!samples.Any()) return CalculateFast(cache, valueExtractor);

            long totalSampleSize = 0;
            const long overheadPerEntry = 120;

            foreach (var kvp in samples)
            {
                var keySize = Encoding.UTF8.GetByteCount(kvp.Key);
                var valueSize = CalculateValueSizeAccurate(valueExtractor(kvp.Value));
                totalSampleSize += overheadPerEntry + keySize + valueSize;
            }

            var averageEntrySize = totalSampleSize / samples.Count;
            return cache.Count * averageEntrySize;
        }

        /// <summary>
        /// Fast estimation for common value types.
        /// </summary>
        private static long EstimateValueSizeFast(object value)
        {
            if (value == null) return IntPtr.Size;
            var type = value.GetType();
            // Variable-size values must be measured on every sample, even when the type was seen before.
            return value switch
            {
                string text => (long)text.Length * 2 + 24,
                byte[] bytes => bytes.LongLength + 24,
                Array array => array.LongLength * IntPtr.Size + 24,
                System.Collections.ICollection collection => (long)collection.Count * IntPtr.Size + 32,
                int or uint or float => 4,
                long or ulong or double => 8,
                short or ushort or char => 2,
                byte or sbyte or bool => 1,
                decimal or DateTimeOffset or Guid => 16,
                DateTime or TimeSpan or TimeOnly => 8,
                DateOnly => 4,
                IntPtr or UIntPtr => IntPtr.Size,
                _ when type.IsEnum => System.Runtime.InteropServices.Marshal.SizeOf(Enum.GetUnderlyingType(type)),
                _ when type.IsValueType => EstimateStructSize(type),
                _ => 100 // Complex object graphs still use a rough fallback, not a deep measurement.
            };
        }

        private static long EstimateStructSize(Type type)
        {
            try
            {
                return System.Runtime.InteropServices.Marshal.SizeOf(type);
            }
            catch (ArgumentException)
            {
                // Generic structs and structs without a supported interop layout still need usable statistics.
                return 100;
            }
        }

        /// <summary>
        /// "Accurate" size calculation using JSON serialization.
        /// 
        /// WARNING: This method name is misleading. JSON serialization size
        /// does NOT accurately represent memory usage:
        /// - JSON is text-based, memory is binary
        /// - JSON doesn't include object overhead, references, padding
        /// - JSON may compress/expand data differently than memory layout
        /// - Serializable != memory footprint
        /// </summary>
        private long CalculateValueSizeAccurate(object value)
        {
            if (value == null) return 8;

            try
            {
                // Use JSON serialization as a reasonable approximation of memory usage
                var json = JsonSerializer.Serialize(value);
                return Encoding.UTF8.GetByteCount(json) + 50; // Add some overhead for object structure
            }
            catch
            {
                // Fallback to fast estimation if serialization fails
                return EstimateValueSizeFast(value);
            }
        }
    }
}
