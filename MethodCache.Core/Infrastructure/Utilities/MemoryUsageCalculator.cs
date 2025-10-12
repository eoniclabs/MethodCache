using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Text.Json;
using System.Threading;
using MethodCache.Core.Configuration;

namespace MethodCache.Core
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
    public class MemoryUsageCalculator
    {
        private readonly MemoryCacheOptions _options;
        private readonly ConcurrentDictionary<Type, long> _typeSizeCache = new();
        private long _operationCounter = 0;
        private long _lastAccurateCalculation = 0;
        private readonly Random _random = new();

        public MemoryUsageCalculator(MemoryCacheOptions options)
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
                MemoryUsageCalculationMode.Fast => CalculateFast(cache),
                MemoryUsageCalculationMode.Accurate => CalculateAccurate(cache, valueExtractor),
                MemoryUsageCalculationMode.Sampling => CalculateSampling(cache, valueExtractor),
                MemoryUsageCalculationMode.Disabled => 0,
                _ => CalculateFast(cache)
            };
        }

        /// <summary>
        /// Fast estimation using fixed constants and type-based estimates.
        /// 
        /// WARNING: This mode uses hardcoded overhead values and rough type-based
        /// size estimates. Results may be significantly different from actual memory usage.
        /// Use only for relative comparisons and performance monitoring trends.
        /// </summary>
        private long CalculateFast<T>(ConcurrentDictionary<string, T> cache)
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
                    var valueSize = EstimateValueSizeFast(kvp.Value!);
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
                return CalculateFast(cache);
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

            if (!samples.Any()) return CalculateFast(cache);

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
        private long EstimateValueSizeFast(object value)
        {
            if (value == null) return 8; // Reference size

            var type = value.GetType();
            
            // Check cache first
            if (_typeSizeCache.TryGetValue(type, out var cachedSize))
            {
                return cachedSize;
            }

            long estimatedSize = value switch
            {
                string str => str.Length * 2 + 24, // Unicode + string overhead
                int => 4,
                long => 8,
                double => 8,
                decimal => 16,
                DateTime => 8,
                DateTimeOffset => 16,
                Guid => 16,
                bool => 1,
                byte[] bytes => bytes.Length + 24,
                Array array => array.Length * 8 + 24, // Rough estimate for object arrays
                ICollection<object> collection => collection.Count * 8 + 32,
                _ when type.IsValueType => System.Runtime.InteropServices.Marshal.SizeOf(type),
                _ => 100 // Default for complex objects
            };

            // Cache the result for this type
            _typeSizeCache.TryAdd(type, estimatedSize);
            return estimatedSize;
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
