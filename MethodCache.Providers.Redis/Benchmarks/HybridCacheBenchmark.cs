using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis.Hybrid;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Benchmarks
{
    public class HybridCacheBenchmark : BaseCacheBenchmark
    {
        private readonly IHybridCacheManager _hybridCacheManager;

        public override string Name => "Hybrid Cache Performance";
        public override string Description => "Benchmarks L1/L2 hybrid cache performance showing cache layer hits";

        public HybridCacheBenchmark(ICacheManager cacheManager, ICacheKeyGenerator keyGenerator, IHybridCacheManager hybridCacheManager)
            : base(cacheManager, keyGenerator)
        {
            _hybridCacheManager = hybridCacheManager;
        }

        public override async Task<BenchmarkResult> RunAsync(BenchmarkOptions options)
        {
            var latencies = new List<TimeSpan>();
            var errors = 0L;
            var errorMessages = new List<string>();
            var l1Hits = 0L;
            var l2Hits = 0L;
            var misses = 0L;

            // Warmup
            await WarmupAsync(options);

            // Pre-populate cache with some L2 data and some L1+L2 data
            var existingKeys = new List<string>();
            var hitCount = (int)(options.Iterations * options.HitRatio);
            
            // Populate L2 cache first (will be L2 hits)
            for (int i = 0; i < hitCount / 2; i++)
            {
                var key = GenerateKey(i, options);
                var value = GenerateValue(i, options);
                var methodName = $"L2Method{i % 5}";
                var args = new object[] { key, i };
                var settings = new CacheMethodSettings 
                { 
                    Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(10),
                };

                // Store in L2 only initially
                await _hybridCacheManager.SetInL2Async(
                    _keyGenerator.GenerateKey(methodName, args, settings), 
                    value, 
                    settings.Duration ?? TimeSpan.FromMinutes(10));
                
                existingKeys.Add($"L2_{methodName}_{i}");
            }

            // Populate L1 cache (will be L1 hits)
            for (int i = hitCount / 2; i < hitCount; i++)
            {
                var key = GenerateKey(i, options);
                var value = GenerateValue(i, options);
                var methodName = $"L1Method{i % 5}";
                var args = new object[] { key, i };
                var settings = new CacheMethodSettings 
                { 
                    Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(10),
                };

                // Access via GetOrCreate to populate both L1 and L2
                await _cacheManager.GetOrCreateAsync(
                    methodName,
                    args,
                    () => Task.FromResult(value),
                    settings,
                    _keyGenerator,
                    false);
                
                existingKeys.Add($"L1_{methodName}_{i}");
            }

            var memoryBefore = GetMemoryUsage();
            var sw = new Stopwatch();

            var semaphore = new SemaphoreSlim(options.ConcurrentOperations);
            var tasks = new List<Task>();
            var operationIndex = 0;

            for (int i = 0; i < options.Iterations; i++)
            {
                var index = Interlocked.Increment(ref operationIndex) - 1;
                if (index >= options.Iterations) break;

                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        string methodName;
                        object[] args;
                        bool expectL1Hit = false;
                        bool expectL2Hit = false;

                        var rand = _random.NextDouble();
                        
                        if (rand < options.HitRatio)
                        {
                            // Should be a hit - determine L1 vs L2
                            if (rand < options.HitRatio / 2)
                            {
                                // L2 hit scenario
                                var l2Index = _random.Next(hitCount / 2);
                                methodName = $"L2Method{l2Index % 5}";
                                var existingKey = GenerateKey(l2Index, options);
                                args = new object[] { existingKey, l2Index };
                                expectL2Hit = true;
                            }
                            else
                            {
                                // L1 hit scenario
                                var l1Index = _random.Next(hitCount / 2, hitCount);
                                methodName = $"L1Method{l1Index % 5}";
                                var existingKey = GenerateKey(l1Index, options);
                                args = new object[] { existingKey, l1Index };
                                expectL1Hit = true;
                            }
                        }
                        else
                        {
                            // Should be a miss
                            methodName = $"MissMethod{index % 5}";
                            var newKey = GenerateKey(index + hitCount, options);
                            args = new object[] { newKey, index + hitCount };
                        }

                        var settings = new CacheMethodSettings 
                        { 
                            Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(10),
                        };

                        var factoryCalled = false;
                        sw.Start();
                        
                        var result = await _cacheManager.GetOrCreateAsync(
                            methodName,
                            args,
                            () =>
                            {
                                factoryCalled = true;
                                return Task.FromResult(GenerateValue(index, options));
                            },
                            settings,
                            _keyGenerator,
                            false);
                        
                        sw.Stop();

                        // Determine what type of hit/miss occurred
                        // This is an approximation based on expectations
                        if (factoryCalled)
                        {
                            Interlocked.Increment(ref misses);
                        }
                        else if (expectL1Hit)
                        {
                            Interlocked.Increment(ref l1Hits);
                        }
                        else if (expectL2Hit)
                        {
                            Interlocked.Increment(ref l2Hits);
                        }
                        else
                        {
                            // Could be either L1 or L2 hit from previous operations
                            Interlocked.Increment(ref l1Hits); // Default to L1
                        }
                        
                        lock (latencies)
                        {
                            latencies.Add(sw.Elapsed);
                        }
                        sw.Reset();
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        lock (errorMessages)
                        {
                            errorMessages.Add($"Hybrid cache operation failed: {ex.Message}");
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            // Get actual hybrid cache statistics
            var hybridStats = await _hybridCacheManager.GetStatsAsync();

            var memoryAfter = GetMemoryUsage();
            var result = CreateResult(Name, latencies, errors, errorMessages);
            result.MemoryUsedBytes = memoryAfter - memoryBefore;
            
            // Add hybrid cache specific metrics
            result.Metrics["l1_hits"] = l1Hits;
            result.Metrics["l2_hits"] = l2Hits;
            result.Metrics["misses"] = misses;
            result.Metrics["total_hits"] = l1Hits + l2Hits;
            result.Metrics["actual_hit_ratio"] = (l1Hits + l2Hits + misses) > 0 ? (double)(l1Hits + l2Hits) / (l1Hits + l2Hits + misses) : 0.0;
            result.Metrics["l1_hit_ratio"] = (l1Hits + l2Hits + misses) > 0 ? (double)l1Hits / (l1Hits + l2Hits + misses) : 0.0;
            result.Metrics["l2_hit_ratio"] = (l1Hits + l2Hits + misses) > 0 ? (double)l2Hits / (l1Hits + l2Hits + misses) : 0.0;
            
            // Add actual hybrid cache statistics
            result.Metrics["hybrid_l1_entries"] = hybridStats.L1Entries;
            result.Metrics["hybrid_l1_hit_ratio"] = hybridStats.L1HitRatio;
            result.Metrics["hybrid_l2_hit_ratio"] = hybridStats.L2HitRatio;
            result.Metrics["hybrid_overall_hit_ratio"] = hybridStats.OverallHitRatio;
            result.Metrics["hybrid_l1_evictions"] = hybridStats.L1Evictions;

            return result;
        }
    }
}