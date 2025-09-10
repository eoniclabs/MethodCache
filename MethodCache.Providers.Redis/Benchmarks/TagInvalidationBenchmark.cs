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
    public class TagInvalidationBenchmark : BaseCacheBenchmark
    {
        public override string Name => "Tag Invalidation";
        public override string Description => "Benchmarks tag-based cache invalidation performance";

        private static readonly string[] CommonTags = { "user", "product", "category", "order", "inventory", "session" };

        public TagInvalidationBenchmark(ICacheManager cacheManager, ICacheKeyGenerator keyGenerator)
            : base(cacheManager, keyGenerator)
        {
        }

        public override async Task<BenchmarkResult> RunAsync(BenchmarkOptions options)
        {
            var latencies = new List<TimeSpan>();
            var errors = 0L;
            var errorMessages = new List<string>();
            var itemsInvalidated = 0L;

            // Warmup
            await WarmupAsync(options);

            // Pre-populate cache with tagged items
            var populatedTags = new List<string>();
            var itemsPerTag = Math.Max(1, options.Iterations / 10); // Distribute items across tags

            for (int i = 0; i < options.Iterations; i++)
            {
                var tagIndex = i % CommonTags.Length;
                var tag = CommonTags[tagIndex];
                var methodName = $"TaggedMethod{i % 5}";
                var args = new object[] { i, GenerateKey(i, options) };
                var value = GenerateValue(i, options);
                
                var settings = new CacheMethodSettings 
                { 
                    Duration = options.DefaultExpiry ?? TimeSpan.FromMinutes(10),
                    Tags = new List<string> { tag }
                };

                if (!populatedTags.Contains(tag))
                {
                    populatedTags.Add(tag);
                }

                await _cacheManager.GetOrCreateAsync(
                    methodName,
                    args,
                    () => Task.FromResult(value),
                    settings,
                    _keyGenerator,
                    false);
            }

            var memoryBefore = GetMemoryUsage();
            var sw = new Stopwatch();

            // Now perform tag invalidations
            var semaphore = new SemaphoreSlim(options.ConcurrentOperations);
            var tasks = new List<Task>();
            var operationIndex = 0;
            var invalidationCount = Math.Min(populatedTags.Count, options.Iterations / 100); // Limit invalidation operations

            for (int i = 0; i < invalidationCount; i++)
            {
                var index = Interlocked.Increment(ref operationIndex) - 1;
                if (index >= invalidationCount) break;

                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var tagToInvalidate = populatedTags[index % populatedTags.Count];

                        sw.Start();
                        await _cacheManager.InvalidateByTagsAsync(tagToInvalidate);
                        sw.Stop();

                        // Estimate items invalidated (approximation)
                        var estimatedItemsInvalidated = itemsPerTag;
                        Interlocked.Add(ref itemsInvalidated, estimatedItemsInvalidated);
                        
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
                            errorMessages.Add($"Tag invalidation failed: {ex.Message}");
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
            result.TotalOperations = invalidationCount; // Override with actual invalidation count
            
            // Add custom metrics
            result.Metrics["items_invalidated"] = itemsInvalidated;
            result.Metrics["items_per_invalidation"] = latencies.Count > 0 ? itemsInvalidated / latencies.Count : 0;
            result.Metrics["populated_tags"] = populatedTags.Count;
            result.Metrics["items_per_tag"] = itemsPerTag;

            return result;
        }
    }
}