using MethodCache.Core;
using MethodCache.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Benchmarks
{
    public abstract class BaseCacheBenchmark : ICacheBenchmark
    {
        protected readonly ICacheManager _cacheManager;
        protected readonly ICacheKeyGenerator _keyGenerator;
        protected readonly Random _random = new();

        public abstract string Name { get; }
        public abstract string Description { get; }

        protected BaseCacheBenchmark(ICacheManager cacheManager, ICacheKeyGenerator keyGenerator)
        {
            _cacheManager = cacheManager;
            _keyGenerator = keyGenerator;
        }

        public abstract Task<BenchmarkResult> RunAsync(BenchmarkOptions options);

        protected string GenerateKey(int index, BenchmarkOptions options)
        {
            if (options.UseRandomKeys)
            {
                return GenerateRandomString(options.KeySize);
            }
            else
            {
                return $"benchmark_key_{index:D10}";
            }
        }

        protected string GenerateValue(int index, BenchmarkOptions options)
        {
            if (options.UseRandomValues)
            {
                return GenerateRandomString(options.ValueSize);
            }
            else
            {
                var baseValue = $"benchmark_value_{index:D10}";
                return baseValue.PadRight(options.ValueSize, 'X');
            }
        }

        protected string GenerateRandomString(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var result = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                result.Append(chars[_random.Next(chars.Length)]);
            }
            return result.ToString();
        }

        protected BenchmarkResult CreateResult(string benchmarkName, List<TimeSpan> latencies, long errors, List<string> errorMessages)
        {
            if (latencies.Count == 0)
            {
                return new BenchmarkResult
                {
                    BenchmarkName = benchmarkName,
                    Errors = errors,
                    ErrorMessages = errorMessages
                };
            }

            latencies.Sort();
            var totalTicks = latencies.Sum(l => l.Ticks);

            var result = new BenchmarkResult
            {
                BenchmarkName = benchmarkName,
                TotalOperations = latencies.Count,
                Duration = TimeSpan.FromTicks(totalTicks),
                MinLatency = latencies.First(),
                MaxLatency = latencies.Last(),
                P50Latency = latencies[latencies.Count / 2],
                P95Latency = latencies[(int)(latencies.Count * 0.95)],
                P99Latency = latencies[(int)(latencies.Count * 0.99)],
                Errors = errors,
                ErrorMessages = errorMessages
            };
            return result;
        }

        protected async Task WarmupAsync(BenchmarkOptions options)
        {
            for (int i = 0; i < options.WarmupIterations; i++)
            {
                try
                {
                    var methodName = $"Warmup{i % 10}";
                    var args = new object[] { i };
                    var value = GenerateValue(i, options);
                    
                    await _cacheManager.GetOrCreateAsync(
                        methodName,
                        args,
                        () => Task.FromResult(value),
                        new CacheMethodSettings { Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(5) },
                        _keyGenerator,
                        false);
                }
                catch
                {
                    // Ignore warmup errors
                }
            }
        }

        protected long GetMemoryUsage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(false);
        }
    }
}