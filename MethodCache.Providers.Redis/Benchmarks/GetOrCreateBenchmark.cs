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
    public class GetOrCreateBenchmark : BaseCacheBenchmark
    {
        public override string Name => "GetOrCreate Operations";
        public override string Description => "Benchmarks cache GetOrCreate operations performance simulating real cache usage patterns";

        public GetOrCreateBenchmark(ICacheManager cacheManager, ICacheKeyGenerator keyGenerator)
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
            var factoryCalls = 0L;

            // Warmup
            await WarmupAsync(options);

            // Pre-populate cache based on hit ratio
            var existingKeys = new List<string>();
            var hitCount = (int)(options.Iterations * options.HitRatio);
            
            for (int i = 0; i < hitCount; i++)
            {
                var key = GenerateKey(i, options);
                var value = GenerateValue(i, options);
                var methodName = $"Method{i % 10}"; // Simulate 10 different methods
                var args = new object[] { key, i };
                var settings = new CacheMethodSettings 
                { 
                    Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(10),
                    Tags = options.Tags.ToList()
                };

                await _cacheManager.GetOrCreateAsync(
                    methodName,
                    args,
                    () => Task.FromResult(value),
                    settings,
                    _keyGenerator,
                    false);
                
                existingKeys.Add(_keyGenerator.GenerateKey(methodName, args, settings));
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
                        if (_random.NextDouble() < options.HitRatio && existingKeys.Count > 0)
                        {
                            // Should be a hit - use existing method/args combination
                            var hitIndex = _random.Next(hitCount);
                            methodName = $"Method{hitIndex % 10}";
                            var existingKey = GenerateKey(hitIndex, options);
                            args = new object[] { existingKey, hitIndex };
                        }
                        else
                        {
                            // Should be a miss - use new method/args combination
                            methodName = $"Method{index % 10}";
                            var newKey = GenerateKey(index + hitCount, options);
                            args = new object[] { newKey, index + hitCount };
                        }

                        var settings = new CacheMethodSettings 
                        { 
                            Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(10),
                            Tags = options.Tags.ToList()
                        };

                        var factoryCalled = false;
                        sw.Start();
                        
                        var result = await _cacheManager.GetOrCreateAsync(
                            methodName,
                            args,
                            () =>
                            {
                                factoryCalled = true;
                                Interlocked.Increment(ref factoryCalls);
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
                            errorMessages.Add($"GetOrCreate operation failed: {ex.Message}");
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
            result.Metrics["factory_calls"] = factoryCalls;
            result.Metrics["actual_hit_ratio"] = hits > 0 || misses > 0 ? (double)hits / (hits + misses) : 0.0;
            result.Metrics["expected_hit_ratio"] = options.HitRatio;

            return result;
        }
    }
}