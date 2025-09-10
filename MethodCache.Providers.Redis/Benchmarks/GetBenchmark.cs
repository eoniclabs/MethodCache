using MethodCache.Core;
using MethodCache.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Benchmarks
{
    public class GetBenchmark : BaseCacheBenchmark
    {
        public override string Name => "Get Operations";
        public override string Description => "Benchmarks cache GET operations performance with configurable hit ratio";

        public GetBenchmark(ICacheManager cacheManager, ICacheKeyGenerator keyGenerator)
            : base(cacheManager, keyGenerator)
        {
        }

        public override async Task<BenchmarkResult> RunAsync(BenchmarkOptions options)
        {
            var latencies = new List<TimeSpan>();
            var errors = 0L;
            var errorMessages = new List<string>();
            var hits = 0L;
            var misses = 0L;

            // Warmup and populate cache with initial data
            await WarmupAsync(options);

            // Pre-populate cache based on hit ratio
            var existingMethodCalls = new List<(string method, object[] args)>();
            var hitCount = (int)(options.Iterations * options.HitRatio);
            
            for (int i = 0; i < hitCount; i++)
            {
                var methodName = $"ExistingMethod{i % 10}";
                var args = new object[] { GenerateKey(i, options), i };
                var value = GenerateValue(i, options);
                var settings = new CacheMethodSettings { Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(5) };
                
                await _cacheManager.GetOrCreateAsync(
                    methodName,
                    args,
                    () => Task.FromResult(value),
                    settings,
                    _keyGenerator,
                    false);
                
                existingMethodCalls.Add((methodName, args));
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
                        
                        // Determine if this should be a hit or miss based on hit ratio
                        if (_random.NextDouble() < options.HitRatio && existingMethodCalls.Count > 0)
                        {
                            // Should be a hit
                            var existing = existingMethodCalls[_random.Next(existingMethodCalls.Count)];
                            methodName = existing.method;
                            args = existing.args;
                        }
                        else
                        {
                            // Should be a miss
                            methodName = $"MissMethod{index % 10}";
                            args = new object[] { GenerateKey(index + hitCount, options), index };
                        }

                        var settings = new CacheMethodSettings { Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(5) };
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

                        if (factoryCalled)
                        {
                            Interlocked.Increment(ref misses);
                        }
                        else
                        {
                            Interlocked.Increment(ref hits);
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
                            errorMessages.Add($"Get operation failed: {ex.Message}");
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

            var memoryAfter = GetMemoryUsage();
            var result = CreateResult(Name, latencies, errors, errorMessages);
            result.MemoryUsedBytes = memoryAfter - memoryBefore;
            
            // Add custom metrics
            result.Metrics["hits"] = hits;
            result.Metrics["misses"] = misses;
            result.Metrics["actual_hit_ratio"] = hits > 0 || misses > 0 ? (double)hits / (hits + misses) : 0.0;
            result.Metrics["expected_hit_ratio"] = options.HitRatio;

            return result;
        }
    }
}