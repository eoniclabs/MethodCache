using MethodCache.Core;
using MethodCache.Providers.Redis.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Benchmarks
{
    public class RedisCacheBenchmarkRunner
    {
        private readonly IServiceProvider _serviceProvider;

        public RedisCacheBenchmarkRunner(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Runs all standard Redis cache benchmarks
        /// </summary>
        public async Task<BenchmarkSuiteResult> RunStandardBenchmarksAsync(BenchmarkOptions? options = null)
        {
            options ??= new BenchmarkOptions
            {
                Iterations = 10000,
                KeySize = 50,
                ValueSize = 1024,
                ConcurrentOperations = 10,
                HitRatio = 0.8,
                DefaultExpiry = TimeSpan.FromMinutes(10)
            };

            var cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
            var keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();

            var suite = new BenchmarkSuite()
                .AddBenchmark(new SetBenchmark(cacheManager, keyGenerator))
                .AddBenchmark(new GetBenchmark(cacheManager, keyGenerator))
                .AddBenchmark(new GetOrCreateBenchmark(cacheManager, keyGenerator))
                .AddBenchmark(new TagInvalidationBenchmark(cacheManager, keyGenerator));

            return await suite.RunAllAsync(options);
        }

        /// <summary>
        /// Runs hybrid cache specific benchmarks
        /// </summary>
        public async Task<BenchmarkSuiteResult> RunHybridBenchmarksAsync(BenchmarkOptions? options = null)
        {
            options ??= new BenchmarkOptions
            {
                Iterations = 10000,
                KeySize = 50,
                ValueSize = 1024,
                ConcurrentOperations = 10,
                HitRatio = 0.8,
                DefaultExpiry = TimeSpan.FromMinutes(10)
            };

            var cacheManager = _serviceProvider.GetRequiredService<ICacheManager>();
            var keyGenerator = _serviceProvider.GetRequiredService<ICacheKeyGenerator>();
            var hybridManager = _serviceProvider.GetService<IHybridCacheManager>();

            if (hybridManager == null)
            {
                throw new InvalidOperationException("Hybrid cache manager is not registered. Use AddHybridRedisCache() in service configuration.");
            }

            var suite = new BenchmarkSuite()
                .AddBenchmark(new HybridCacheBenchmark(cacheManager, keyGenerator, hybridManager))
                .AddBenchmark(new SetBenchmark(cacheManager, keyGenerator))
                .AddBenchmark(new GetBenchmark(cacheManager, keyGenerator))
                .AddBenchmark(new GetOrCreateBenchmark(cacheManager, keyGenerator));

            return await suite.RunAllAsync(options);
        }

        /// <summary>
        /// Runs performance comparison between different cache configurations
        /// </summary>
        public async Task RunPerformanceComparisonAsync()
        {
            Console.WriteLine("=== REDIS CACHE PERFORMANCE COMPARISON ===");
            Console.WriteLine();

            var lightOptions = new BenchmarkOptions
            {
                Iterations = 1000,
                KeySize = 20,
                ValueSize = 256,
                ConcurrentOperations = 5,
                HitRatio = 0.9
            };

            var standardOptions = new BenchmarkOptions
            {
                Iterations = 10000,
                KeySize = 50,
                ValueSize = 1024,
                ConcurrentOperations = 10,
                HitRatio = 0.8
            };

            var heavyOptions = new BenchmarkOptions
            {
                Iterations = 5000,
                KeySize = 100,
                ValueSize = 4096,
                ConcurrentOperations = 20,
                HitRatio = 0.7
            };

            await RunBenchmarkScenario("Light Load", lightOptions);
            await RunBenchmarkScenario("Standard Load", standardOptions);
            await RunBenchmarkScenario("Heavy Load", heavyOptions);
        }

        private async Task RunBenchmarkScenario(string scenarioName, BenchmarkOptions options)
        {
            Console.WriteLine($"\n--- {scenarioName} Scenario ---");
            Console.WriteLine($"Iterations: {options.Iterations:N0}, Key Size: {options.KeySize}, Value Size: {options.ValueSize:N0} bytes");
            Console.WriteLine($"Concurrent Operations: {options.ConcurrentOperations}, Hit Ratio: {options.HitRatio:P0}");
            
            try
            {
                var results = await RunStandardBenchmarksAsync(options);
                results.PrintSummary();

                // Try hybrid benchmarks if available
                try
                {
                    Console.WriteLine("\n--- Hybrid Cache Results ---");
                    var hybridResults = await RunHybridBenchmarksAsync(options);
                    hybridResults.PrintSummary();
                }
                catch (InvalidOperationException)
                {
                    Console.WriteLine("Hybrid cache not configured - skipping hybrid benchmarks");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error running {scenarioName} scenario: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs a quick performance test with minimal setup
        /// </summary>
        public async Task RunQuickPerformanceTestAsync()
        {
            var options = new BenchmarkOptions
            {
                Iterations = 1000,
                KeySize = 50,
                ValueSize = 1024,
                ConcurrentOperations = 5,
                WarmupIterations = 100,
                HitRatio = 0.8
            };

            Console.WriteLine("=== QUICK PERFORMANCE TEST ===");
            Console.WriteLine($"Running {options.Iterations:N0} iterations with {options.ConcurrentOperations} concurrent operations...");
            Console.WriteLine();

            var results = await RunStandardBenchmarksAsync(options);
            results.PrintSummary();
        }
    }

    public static class BenchmarkServiceCollectionExtensions
    {
        /// <summary>
        /// Adds benchmarking services to the service collection
        /// </summary>
        public static IServiceCollection AddRedisCacheBenchmarks(this IServiceCollection services)
        {
            services.AddSingleton<RedisCacheBenchmarkRunner>();
            return services;
        }
    }
}