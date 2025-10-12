using System;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Core.Tests.Core
{
    /// <summary>
    /// Integration test demonstrating the new memory usage calculation functionality.
    /// </summary>
    public class MemoryUsageIntegrationTest
    {
        private readonly ITestOutputHelper _output;

        public MemoryUsageIntegrationTest(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task InMemoryCacheManager_ShouldCalculateMemoryUsageWithDifferentModes()
        {
            // Test all memory calculation modes with actual cache manager
            var modes = new[]
            {
                MemoryUsageCalculationMode.Fast,
                MemoryUsageCalculationMode.Accurate,
                MemoryUsageCalculationMode.Sampling,
                MemoryUsageCalculationMode.Disabled
            };

            foreach (var mode in modes)
            {
                _output.WriteLine($"Testing {mode} mode:");

                // Arrange
                var services = new ServiceCollection();
                services.AddSingleton<ICacheMetricsProvider, TestCacheMetricsProvider>();
                services.Configure<MemoryCacheOptions>(options =>
                {
                    options.MemoryCalculationMode = mode;
                    options.SamplingPercentage = 0.2; // 20% for sampling
                    options.AccurateModeRecalculationInterval = 1; // Force recalculation
                    options.EnableStatistics = mode != MemoryUsageCalculationMode.Disabled;
                });
                services.AddSingleton<IMemoryCache, InMemoryCacheManager>();

                var serviceProvider = services.BuildServiceProvider();
                var cache = serviceProvider.GetRequiredService<IMemoryCache>();

                // Act - Populate cache with test data
                await PopulateCacheWithTestData(cache);

                // Get memory usage statistics
                var stats = await cache.GetStatsAsync();

                // Assert
                _output.WriteLine($"  Entries: {stats.Entries}");
                _output.WriteLine($"  Memory Usage: {stats.MemoryUsage:N0} bytes");
                _output.WriteLine($"  Hit Ratio: {stats.HitRatio:P1}");

                Assert.True(stats.Entries > 0, "Should have cache entries");
                
                if (mode == MemoryUsageCalculationMode.Disabled)
                {
                    Assert.Equal(0, stats.MemoryUsage);
                }
                else
                {
                    Assert.True(stats.MemoryUsage > 0, $"{mode} mode should return positive memory usage");
                }

                // Cleanup
                await cache.ClearAsync();
                if (serviceProvider is IDisposable disposable)
                    disposable.Dispose();

                _output.WriteLine("");
            }
        }

        [Fact]
        public async Task MemoryUsageCalculation_ShouldReflectCacheSize()
        {
            // Arrange
            var services = new ServiceCollection();
            services.AddSingleton<ICacheMetricsProvider, TestCacheMetricsProvider>();
            services.Configure<MemoryCacheOptions>(options =>
            {
                options.MemoryCalculationMode = MemoryUsageCalculationMode.Fast;
                options.EnableStatistics = true;
            });
            services.AddSingleton<IMemoryCache, InMemoryCacheManager>();

            var serviceProvider = services.BuildServiceProvider();
            var cache = serviceProvider.GetRequiredService<IMemoryCache>();

            // Act & Assert - Memory usage should increase with more entries
            var initialStats = await cache.GetStatsAsync();
            Assert.Equal(0, initialStats.MemoryUsage);

            // Add some entries
            for (int i = 0; i < 10; i++)
            {
                await cache.SetAsync($"key_{i}", $"value_{i}", TimeSpan.FromMinutes(5));
            }

            var smallCacheStats = await cache.GetStatsAsync();
            Assert.True(smallCacheStats.MemoryUsage > 0);

            // Add more entries
            for (int i = 10; i < 100; i++)
            {
                await cache.SetAsync($"key_{i}", $"Large value with more content {i} to increase memory usage", TimeSpan.FromMinutes(5));
            }

            var largeCacheStats = await cache.GetStatsAsync();
            Assert.True(largeCacheStats.MemoryUsage > smallCacheStats.MemoryUsage);

            _output.WriteLine($"Empty cache: {initialStats.MemoryUsage:N0} bytes");
            _output.WriteLine($"Small cache (10 entries): {smallCacheStats.MemoryUsage:N0} bytes");
            _output.WriteLine($"Large cache (100 entries): {largeCacheStats.MemoryUsage:N0} bytes");

            // Cleanup
            if (serviceProvider is IDisposable disposable)
                disposable.Dispose();
        }

        private async Task PopulateCacheWithTestData(IMemoryCache cache)
        {
            // Add various types of data
            for (int i = 0; i < 50; i++)
            {
                var key = $"test_key_{i}";
                var expiration = TimeSpan.FromMinutes(30);

                var value = (i % 4) switch
                {
                    0 => $"String value {i} with some content",
                    1 => i,
                    2 => new { Id = i, Name = $"Object_{i}", Timestamp = DateTime.UtcNow },
                    3 => new byte[100], // Byte array
                    _ => (object)i
                };

                await cache.SetAsync(key, value, expiration);
            }

            // Simulate some cache hits
            for (int i = 0; i < 10; i++)
            {
                await cache.GetAsync<object>($"test_key_{i}");
            }
        }

        private class TestCacheMetricsProvider : ICacheMetricsProvider
        {
            public void CacheHit(string methodName) { }
            public void CacheMiss(string methodName) { }
            public void CacheError(string methodName, string error) { }
            public void CacheLatency(string methodName, long elapsedMilliseconds) { }
        }
    }
}
