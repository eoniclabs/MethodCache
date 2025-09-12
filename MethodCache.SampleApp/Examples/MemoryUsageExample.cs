using System;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MethodCache.SampleApp.Examples
{
    /// <summary>
    /// Demonstrates different memory usage calculation modes and their performance characteristics.
    /// </summary>
    public class MemoryUsageExample
    {
        public static async Task RunExampleAsync()
        {
            Console.WriteLine("=== Memory Usage Calculation Example ===");
            Console.WriteLine();

            // Example 1: Fast Mode (Default)
            await DemonstrateMode(MemoryUsageCalculationMode.Fast, "Production High-Performance");

            // Example 2: Accurate Mode
            await DemonstrateMode(MemoryUsageCalculationMode.Accurate, "Development/Monitoring");

            // Example 3: Sampling Mode
            await DemonstrateMode(MemoryUsageCalculationMode.Sampling, "Balanced Production");

            // Example 4: Disabled Mode
            await DemonstrateMode(MemoryUsageCalculationMode.Disabled, "Maximum Performance");
        }

        private static async Task DemonstrateMode(MemoryUsageCalculationMode mode, string scenario)
        {
            Console.WriteLine($"--- {mode} Mode ({scenario}) ---");

            // Configure services with the specific mode
            var services = new ServiceCollection();
            services.AddSingleton<ICacheMetricsProvider, ConsoleCacheMetricsProvider>();
            
            services.Configure<MemoryCacheOptions>(options =>
            {
                options.MemoryCalculationMode = mode;
                options.SamplingPercentage = 0.1; // 10% for sampling mode
                options.AccurateModeRecalculationInterval = 100; // Recalculate every 100 operations
                options.EnableStatistics = mode != MemoryUsageCalculationMode.Disabled;
            });

            services.AddSingleton<IMemoryCache, InMemoryCacheManager>();

            var serviceProvider = services.BuildServiceProvider();
            var cache = serviceProvider.GetRequiredService<IMemoryCache>();

            // Populate cache with test data
            await PopulateCacheWithTestData(cache);

            // Measure performance of memory calculation
            var startTime = DateTime.UtcNow;
            var stats = await cache.GetStatsAsync();
            var endTime = DateTime.UtcNow;

            Console.WriteLine($"  Entries: {stats.Entries:N0}");
            Console.WriteLine($"  Memory Usage: {stats.MemoryUsage:N0} bytes ({stats.MemoryUsage / 1024.0 / 1024.0:F2} MB)");
            Console.WriteLine($"  Calculation Time: {(endTime - startTime).TotalMilliseconds:F2}ms");
            Console.WriteLine($"  Hit Ratio: {stats.HitRatio:P1}");
            Console.WriteLine();

            // Cleanup
            await cache.ClearAsync();
            if (serviceProvider is IDisposable disposable)
                disposable.Dispose();
        }

        private static async Task PopulateCacheWithTestData(IMemoryCache cache)
        {
            var random = new Random(42); // Fixed seed for consistent results

            // Add various types of data to test memory calculation
            for (int i = 0; i < 1000; i++)
            {
                var key = $"test_key_{i}";
                var expiration = TimeSpan.FromMinutes(30);

                var value = (i % 5) switch
                {
                    0 => $"String value {i} with some additional content to make it more realistic for testing purposes",
                    1 => i,
                    2 => new TestObject { Id = i, Name = $"Object_{i}", Data = GenerateRandomBytes(random, 100) },
                    3 => new { Id = i, Timestamp = DateTime.UtcNow, Value = random.NextDouble() },
                    4 => Enumerable.Range(0, 10).Select(x => $"Item_{i}_{x}").ToList(),
                    _ => (object)i
                };

                await cache.SetAsync(key, value, expiration);
            }

            // Simulate some cache hits to populate statistics
            for (int i = 0; i < 100; i++)
            {
                var key = $"test_key_{random.Next(1000)}";
                await cache.GetAsync<object>(key);
            }
        }

        private static byte[] GenerateRandomBytes(Random random, int size)
        {
            var bytes = new byte[size];
            random.NextBytes(bytes);
            return bytes;
        }

        private class TestObject
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public byte[] Data { get; set; } = Array.Empty<byte>();
        }

        private class ConsoleCacheMetricsProvider : ICacheMetricsProvider
        {
            public void CacheHit(string methodName) { }
            public void CacheMiss(string methodName) { }
            public void CacheError(string methodName, string error) { }
            public void CacheLatency(string methodName, long elapsedMilliseconds) { }
        }
    }
}
