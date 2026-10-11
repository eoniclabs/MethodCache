using System.Diagnostics;
using BitFaster.Caching;
using BitFaster.Caching.Lfu;
using BitFaster.Caching.Lru;
using MethodCache.Benchmarks.Microbenchmarks.Legacy;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Memory;

using MethodCacheOptions = MethodCache.MethodCacheMemoryOptions;
using MsMemoryCacheOptions = Microsoft.Extensions.Caching.Memory.MemoryCacheOptions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Benchmarks.Microbenchmarks;

/// <summary>
/// Measures hit ratio on synthetic traces (Zipf and Zipf with scans, the methodology used by Caffeine)
/// and multi-threaded read throughput. Run with: <c>dotnet run -c Release -- hitratio</c>.
/// </summary>
public static class HitRatioSimulation
{
    private sealed record Contender(string Name, Func<int, (Func<string, bool> Get, Action<string> Set, Action Dispose)> Create);

    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private static readonly Contender[] Contenders =
    {
        new("MethodCache (W-TinyLFU)", capacity => FromManager(new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheOptions { MaxItems = capacity })))),
        new("MethodCache (LRU)", capacity => FromManager(new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheOptions { MaxItems = capacity, EvictionPolicy = MemoryCacheEvictionPolicy.Lru })))),
        new("Previous engine (LRU)", capacity =>
        {
            var legacy = new LegacyInMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheOptions { MaxItems = capacity, EvictionPolicy = MemoryCacheEvictionPolicy.Lru }));
            return (key => legacy.TryGetFast<string>(key, out _), key => legacy.SetAsync(key, key, Ttl).GetAwaiter().GetResult(), legacy.Dispose);
        }),
        new("MS MemoryCache (SizeLimit)", capacity =>
        {
            var cache = new MemoryCache(new MsMemoryCacheOptions { SizeLimit = capacity });
            return (key => cache.TryGetValue(key, out _), key => cache.Set(key, key, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Ttl }), cache.Dispose);
        }),
        new("BitFaster ConcurrentLfu", capacity =>
        {
            var cache = new ConcurrentLfu<string, string>(capacity);
            return (key => cache.TryGet(key, out _), key => cache.AddOrUpdate(key, key), () => { });
        }),
        new("BitFaster ConcurrentLru", capacity =>
        {
            var cache = new ConcurrentLru<string, string>(capacity);
            return (key => cache.TryGet(key, out _), key => cache.AddOrUpdate(key, key), () => { });
        })
    };

    private static (Func<string, bool>, Action<string>, Action) FromManager(InMemoryCacheManager manager) =>
        (key => manager.TryGetFast<string>(key, out _), key => manager.SetAsync(key, key, Ttl).GetAwaiter().GetResult(), manager.Dispose);

    public static void Run()
    {
        const int keySpace = 100_000;
        const int traceLength = 2_000_000;

        var zipf = ZipfTrace(keySpace, traceLength, 0.9, seed: 42);
        var zipfWithScans = WithScans(zipf, scanEvery: 50_000, scanLength: 20_000);

        Console.WriteLine();
        Console.WriteLine("Hit ratio (higher is better)");
        Console.WriteLine($"{"Cache",-28} {"Zipf 0.9, 1k",14} {"Zipf 0.9, 10k",14} {"Zipf+scan, 1k",14} {"Zipf+scan, 10k",15}");
        foreach (var contender in Contenders)
        {
            var results = new[]
            {
                HitRatio(contender, zipf, 1_000),
                HitRatio(contender, zipf, 10_000),
                HitRatio(contender, zipfWithScans, 1_000),
                HitRatio(contender, zipfWithScans, 10_000)
            };
            Console.WriteLine($"{contender.Name,-28} {results[0],14:P1} {results[1],14:P1} {results[2],14:P1} {results[3],15:P1}");
        }

        Console.WriteLine();
        Console.WriteLine($"Read throughput, all hits, {Environment.ProcessorCount} threads (million reads/second, higher is better)");
        foreach (var contender in Contenders)
        {
            Console.WriteLine($"{contender.Name,-28} {Throughput(contender, Environment.ProcessorCount),8:F1}");
        }
    }

    private static double HitRatio(Contender contender, string[] trace, int capacity)
    {
        var (get, set, dispose) = contender.Create(capacity);
        long hits = 0;
        foreach (var key in trace)
        {
            if (get(key))
            {
                hits++;
            }
            else
            {
                set(key);
            }
        }

        dispose();
        return (double)hits / trace.Length;
    }

    private static double Throughput(Contender contender, int threads)
    {
        // Median of several short trials: a single trial is too sensitive to scheduler noise.
        const int keys = 1_000;
        const int trials = 5;
        var (get, set, dispose) = contender.Create(10_000);
        var keySet = Enumerable.Range(0, keys).Select(i => $"user:{i}:profile").ToArray();
        foreach (var key in keySet)
        {
            set(key);
        }

        var results = new double[trials];
        for (var trial = 0; trial < trials; trial++)
        {
            var duration = TimeSpan.FromSeconds(1);
            var counts = new long[threads];
            var start = new Barrier(threads);
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                var sw = Stopwatch.StartNew();
                long count = 0;
                var i = t * 97;
                while (sw.Elapsed < duration)
                {
                    for (var j = 0; j < 1_000; j++)
                    {
                        get(keySet[i++ % keys]);
                    }

                    count += 1_000;
                }

                counts[t] = count;
            })).ToArray();

            foreach (var worker in workers) worker.Start();
            foreach (var worker in workers) worker.Join();
            results[trial] = counts.Sum() / duration.TotalSeconds / 1_000_000;
        }

        dispose();
        Array.Sort(results);
        return results[trials / 2];
    }

    private static string[] ZipfTrace(int keySpace, int length, double exponent, int seed)
    {
        // Inverse-CDF sampling over a precomputed cumulative distribution.
        var cdf = new double[keySpace];
        double sum = 0;
        for (var i = 0; i < keySpace; i++)
        {
            sum += 1.0 / Math.Pow(i + 1, exponent);
            cdf[i] = sum;
        }

        var random = new Random(seed);
        var names = Enumerable.Range(0, keySpace).Select(i => $"k{i}").ToArray();
        var trace = new string[length];
        for (var i = 0; i < length; i++)
        {
            var index = Array.BinarySearch(cdf, random.NextDouble() * sum);
            trace[i] = names[index < 0 ? ~index : index];
        }

        return trace;
    }

    private static string[] WithScans(string[] trace, int scanEvery, int scanLength)
    {
        var result = new List<string>(trace.Length * 2);
        var scanId = 0;
        for (var i = 0; i < trace.Length; i++)
        {
            result.Add(trace[i]);
            if (i % scanEvery == scanEvery - 1)
            {
                for (var j = 0; j < scanLength; j++)
                {
                    result.Add($"scan{scanId++}");
                }
            }
        }

        return result.ToArray();
    }
}
