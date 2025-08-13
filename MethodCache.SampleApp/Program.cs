using System;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.SampleApp.Interfaces;
using MethodCache.SampleApp.Services;
using MethodCache.SampleApp.Infrastructure;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("🚀 MethodCache Sample Application");
            Console.WriteLine("=================================");
            Console.WriteLine("This sample demonstrates core MethodCache concepts without source generation.");
            Console.WriteLine();
            
            var services = new ServiceCollection();

            // Configure MethodCache with custom settings
            services.AddMethodCache(config =>
            {
                config.DefaultDuration(TimeSpan.FromMinutes(10));
                config.DefaultKeyGenerator<CustomKeyGenerator>();
            });

            // Register custom infrastructure components (these override the defaults)
            services.AddSingleton<ICacheManager, CustomCacheManager>();
            services.AddSingleton<ICacheKeyGenerator, CustomKeyGenerator>();
            services.AddSingleton<ICacheMetricsProvider, EnhancedMetricsProvider>();
            
            // Register simple cached service for demonstration
            services.AddSingleton<ISimpleCachedService, SimpleCachedService>();

            var serviceProvider = services.BuildServiceProvider();
            
            Console.WriteLine("✅ Services configured and built\n");

            // Run demonstrations
            await RunBasicCachingDemo(serviceProvider);
            await RunCacheInvalidationDemo(serviceProvider);
            await RunCacheMetricsDemo(serviceProvider);
            await RunCustomCacheManagerDemo(serviceProvider);
            
            Console.WriteLine("\n🎉 MethodCache Sample Application Complete!");
            Console.WriteLine("This demonstrates the core MethodCache framework concepts.");
            Console.WriteLine("For production use with source generation, ensure proper interface and service configuration.");
        }
        
        private static async Task RunBasicCachingDemo(ServiceProvider serviceProvider)
        {
            Console.WriteLine("🔍 === BASIC CACHING DEMO ===");
            
            var cachedService = serviceProvider.GetRequiredService<ISimpleCachedService>();
            
            // Test expensive data caching
            Console.WriteLine("\n📊 Testing Expensive Data Caching:");
            Console.WriteLine("First call (cache miss):");
            var data1 = await cachedService.GetExpensiveDataAsync("test-key");
            Console.WriteLine($"Result: {data1}");
            
            Console.WriteLine("\nSecond call (cache hit):");
            var data2 = await cachedService.GetExpensiveDataAsync("test-key");
            Console.WriteLine($"Result: {data2}");
            
            Console.WriteLine("\nThird call with different key (cache miss):");
            var data3 = await cachedService.GetExpensiveDataAsync("another-key");
            Console.WriteLine($"Result: {data3}");
            
            // Test user caching
            Console.WriteLine("\n👤 Testing User Caching:");
            Console.WriteLine("First user lookup (cache miss):");
            var user1 = await cachedService.GetUserAsync(1);
            Console.WriteLine($"User: {user1?.Name} ({user1?.Email})");
            
            Console.WriteLine("\nSecond user lookup (cache hit):");
            var user1Again = await cachedService.GetUserAsync(1);
            Console.WriteLine($"User: {user1Again?.Name} ({user1Again?.Email})");
            
            Console.WriteLine("\nDifferent user lookup (cache miss):");
            var user2 = await cachedService.GetUserAsync(2);
            Console.WriteLine($"User: {user2?.Name} ({user2?.Email})");
        }
        
        private static async Task RunCacheInvalidationDemo(ServiceProvider serviceProvider)
        {
            Console.WriteLine("\n🗑️ === CACHE INVALIDATION DEMO ===");
            
            var cachedService = serviceProvider.GetRequiredService<ISimpleCachedService>();
            
            // Setup cached data
            Console.WriteLine("\nSetting up cached user data:");
            var user = await cachedService.GetUserAsync(3);
            Console.WriteLine($"User cached: {user?.Name}");
            
            // Verify cache hit
            Console.WriteLine("\nVerifying cache hit:");
            var userCached = await cachedService.GetUserAsync(3);
            Console.WriteLine($"User from cache: {userCached?.Name}");
            
            // Invalidate user cache by tag
            Console.WriteLine("\nInvalidating all user cache entries:");
            await cachedService.InvalidateUserCacheAsync();
            
            // Verify cache miss after invalidation
            Console.WriteLine("\nUser lookup after invalidation (should be cache miss):");
            var userAfterInvalidation = await cachedService.GetUserAsync(3);
            Console.WriteLine($"User after invalidation: {userAfterInvalidation?.Name}");
            
            // Test setting up cache again
            Console.WriteLine("\nSetting up cache again:");
            await cachedService.GetUserAsync(4);
            
            // Clear cache by tags
            Console.WriteLine("\nClearing cache by tags:");
            await cachedService.InvalidateAllCacheAsync();
            
            // Verify cache cleared
            Console.WriteLine("\nUser lookup after tag-based clear (should be cache miss):");
            var userAfterClear = await cachedService.GetUserAsync(4);
            Console.WriteLine($"User after clear: {userAfterClear?.Name}");
        }
        
        private static async Task RunCacheMetricsDemo(ServiceProvider serviceProvider)
        {
            Console.WriteLine("\n📈 === CACHE METRICS DEMO ===");
            
            var metricsProvider = serviceProvider.GetRequiredService<ICacheMetricsProvider>();
            var cachedService = serviceProvider.GetRequiredService<ISimpleCachedService>();
            
            if (metricsProvider is EnhancedMetricsProvider enhancedMetrics)
            {
                Console.WriteLine("\n📊 Initial Metrics:");
                var initialSummary = enhancedMetrics.GetMetricsSummary();
                PrintMetricsSummary(initialSummary);
                
                // Generate some cache activity
                Console.WriteLine("\nGenerating cache activity...");
                await cachedService.GetExpensiveDataAsync("metrics-test-1");
                await cachedService.GetExpensiveDataAsync("metrics-test-1"); // hit
                await cachedService.GetExpensiveDataAsync("metrics-test-2"); // miss
                await cachedService.GetUserAsync(10);
                await cachedService.GetUserAsync(10); // hit
                await cachedService.GetUserAsync(11); // miss
                
                Console.WriteLine("\n📊 Final Metrics:");
                var finalSummary = enhancedMetrics.GetMetricsSummary();
                PrintMetricsSummary(finalSummary);
            }
        }
        
        private static async Task RunCustomCacheManagerDemo(ServiceProvider serviceProvider)
        {
            Console.WriteLine("\n🛠️ === CUSTOM CACHE MANAGER DEMO ===");
            
            var cacheManager = serviceProvider.GetRequiredService<ICacheManager>();
            var cachedService = serviceProvider.GetRequiredService<ISimpleCachedService>();
            
            if (cacheManager is CustomCacheManager customCacheManager)
            {
                Console.WriteLine("\n📊 Initial Cache Statistics:");
                var stats = customCacheManager.GetStatistics();
                Console.WriteLine($"Total Entries: {stats.TotalEntries}");
                Console.WriteLine($"Active Entries: {stats.ActiveEntries}");
                Console.WriteLine($"Expired Entries: {stats.ExpiredEntries}");
                Console.WriteLine($"Estimated Memory Usage: {stats.EstimatedMemoryUsageBytes:N0} bytes");
                
                Console.WriteLine("\n🔧 Testing Tag-based Operations through cached service:");
                
                // Create some cached data with tags through the service
                Console.WriteLine("Creating cached data that will have tags...");
                await cachedService.GetUserAsync(100);
                await cachedService.GetUserAsync(101);
                await cachedService.GetExpensiveDataAsync("custom-test");
                
                var statsAfterCreation = customCacheManager.GetStatistics();
                Console.WriteLine($"Active entries after creation: {statsAfterCreation.ActiveEntries}");
                
                // Test tag-based invalidation
                Console.WriteLine("\nInvalidating all 'users' tagged entries...");
                await customCacheManager.InvalidateByTagsAsync("users");
                
                var statsAfterInvalidation = customCacheManager.GetStatistics();
                Console.WriteLine($"Active entries after 'users' tag invalidation: {statsAfterInvalidation.ActiveEntries}");
                
                // Verify that user data is invalidated but other data remains
                Console.WriteLine("\nTesting cache state after tag invalidation:");
                Console.WriteLine("User lookup (should be cache miss):");
                await cachedService.GetUserAsync(100);
                
                Console.WriteLine("Expensive data lookup (should be cache hit if not tagged as 'users'):");
                await cachedService.GetExpensiveDataAsync("custom-test");
            }
        }
        
        private static void PrintMetricsSummary(CacheMetricsSummary summary)
        {
            Console.WriteLine($"  Total Hits: {summary.TotalHits:N0}");
            Console.WriteLine($"  Total Misses: {summary.TotalMisses:N0}");
            Console.WriteLine($"  Total Errors: {summary.TotalErrors:N0}");
            Console.WriteLine($"  Overall Hit Ratio: {summary.OverallHitRatio:P2}");
            
            if (summary.MethodMetrics.Any())
            {
                Console.WriteLine("  Method Breakdown:");
                foreach (var method in summary.MethodMetrics.Take(5))
                {
                    Console.WriteLine($"    {method.MethodName}: {method.HitCount} hits, {method.MissCount} misses (Ratio: {method.HitRatio:P1})");
                }
            }
        }
    }
}