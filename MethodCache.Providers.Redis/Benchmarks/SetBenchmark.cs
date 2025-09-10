using MethodCache.Core;
using MethodCache.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Benchmarks
{
    public class SetBenchmark : BaseCacheBenchmark
    {
        public override string Name => "Set Operations";
        public override string Description => "Benchmarks cache SET operations performance";

        public SetBenchmark(ICacheManager cacheManager, ICacheKeyGenerator keyGenerator)
            : base(cacheManager, keyGenerator)
        {
        }

        public override async Task<BenchmarkResult> RunAsync(BenchmarkOptions options)
        {
            var latencies = new List<TimeSpan>();
            var errors = 0L;
            var errorMessages = new List<string>();

            // Warmup
            await WarmupAsync(options);

            var memoryBefore = GetMemoryUsage();
            var sw = new Stopwatch();

            // Prepare data
            var keys = new string[options.Iterations];
            var values = new string[options.Iterations];
            
            for (int i = 0; i < options.Iterations; i++)
            {
                keys[i] = GenerateKey(i, options);
                values[i] = GenerateValue(i, options);
            }

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
                        var methodName = $"SetBenchmarkMethod{index % 10}";
                        var args = new object[] { keys[index], index };
                        var settings = new CacheMethodSettings { Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(5) };

                        sw.Start();
                        await _cacheManager.GetOrCreateAsync(
                            methodName,
                            args,
                            () => Task.FromResult(values[index]),
                            settings,
                            _keyGenerator,
                            false);
                        sw.Stop();
                        
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
                            errorMessages.Add($"Set operation failed: {ex.Message}");
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

            return result;
        }
    }
}