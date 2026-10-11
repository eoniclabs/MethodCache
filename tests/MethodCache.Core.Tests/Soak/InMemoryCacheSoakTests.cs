using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using MethodCache.Core.Tests.Core;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;
using Xunit.Abstractions;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Soak;

/// <summary>
/// Runs <see cref="InMemoryCacheManager"/> under sustained concurrent load on the real clock: loads, reads, writes,
/// failing loads and key, tag and pattern invalidations from every core, with entries expiring and being evicted
/// throughout. Checks that the weight bound holds and memory stops growing, that no unexpected exception escapes,
/// that a key's factory never runs twice at once (stampede protection), that every caller gets its own key's value,
/// and that the hit and miss counters add up to what the callers saw.
/// </summary>
/// <remarks>
/// Skipped unless <c>METHODCACHE_SOAK_SECONDS</c> is set, so the normal test run stays fast and deterministic. The
/// soak job runs it for several minutes (<c>build/ci/jobs.sh soak</c>); locally:
/// <code>
/// METHODCACHE_SOAK_SECONDS=10 dotnet test tests/MethodCache.Core.Tests -f net10.0 --filter "Category=Soak"
/// </code>
/// </remarks>
[Collection(nameof(CpuIntensiveCollection))]
[Trait("Category", "Soak")]
public class InMemoryCacheSoakTests
{
    private const int HotKeys = 32;
    private const int ColdKeys = 20_000;
    private const int ColdTagGroups = 16;
    private const int MaxValueBytes = 4_096;
    private const long WeightBound = 2 * 1024 * 1024;

    private static readonly CacheRuntimePolicy HotPolicy = CacheRuntimePolicy.Empty("Soak.Hot") with
    {
        Duration = TimeSpan.FromMilliseconds(200),
        Tags = new[] { "hot" },
    };

    private static readonly CacheRuntimePolicy[] ColdPolicies = Enumerable.Range(0, ColdTagGroups)
        .Select(group => CacheRuntimePolicy.Empty("Soak.Cold") with
        {
            Duration = TimeSpan.FromSeconds(2),
            SlidingExpiration = TimeSpan.FromMilliseconds(500),
            Tags = new[] { "cold", $"cold:{group}" },
        })
        .ToArray();

    private readonly ITestOutputHelper _output;

    public InMemoryCacheSoakTests(ITestOutputHelper output) => _output = output;

    /// <summary>Thrown by the cold factories now and then; the only exception callers may see.</summary>
    private sealed class SoakFactoryException : Exception
    {
        public SoakFactoryException() : base("soak factory failure") { }
    }

    private sealed class Counters
    {
        public long LoadCalls;
        public long LoadSucceeded;
        public long FactoryCalls;
        public long FactoryFailures;
        public long HotLoadCalls;
        public long HotFactoryCalls;
        public long GetCalls;
        public long GetMisses;
        public long TryGetHits;
        public long Writes;
        public long Invalidations;
        public long StampedeViolations;
        public long WrongValues;
        public readonly int[] HotInFlight = new int[HotKeys];
        public readonly ConcurrentQueue<Exception> Unexpected = new();
    }

    [SoakFact]
    public async Task SustainedConcurrentLoad_StaysBoundedConsistentAndStampedeFree()
    {
        var duration = SoakFactAttribute.Duration!.Value;
        var options = new MethodCacheMemoryOptions
        {
            Weigher = static (key, value) => 64 + (2L * key.Length) + (value is byte[] bytes ? bytes.Length : 0),
            MaxMemoryBytes = WeightBound,
            DefaultExpiration = TimeSpan.FromSeconds(2),
            CleanupInterval = TimeSpan.FromSeconds(10),
        };
        using var cache = new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(options));
        var counters = new Counters();
        using var stop = new CancellationTokenSource();

        var workers = Enumerable.Range(0, Math.Max(4, Environment.ProcessorCount * 2))
            .Select(i => Task.Run(() => WorkerAsync(cache, counters, seed: i, stop.Token)))
            .Append(Task.Run(() => InvalidatorAsync(cache, counters, stop.Token)))
            .ToArray();

        // Managed memory after a full collection, sampled ten times from a tenth of the run on (once the cache has
        // filled up to its bound) and once more after the load stops
        var samples = new List<long>();
        long maxWeight = 0;
        long maxEntries = 0;
        var watch = Stopwatch.StartNew();
        var nextSample = TimeSpan.FromTicks(duration.Ticks / 10);
        while (watch.Elapsed < duration && counters.Unexpected.IsEmpty)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            maxWeight = Math.Max(maxWeight, cache.Engine.TotalWeight);
            maxEntries = Math.Max(maxEntries, cache.Engine.Count);
            if (watch.Elapsed >= nextSample)
            {
                samples.Add(GC.GetTotalMemory(forceFullCollection: true));
                nextSample += TimeSpan.FromTicks(duration.Ticks / 10);
            }
        }

        stop.Cancel();
        await Task.WhenAll(workers);
        cache.Engine.Maintain();

        var stats = await cache.GetStatsAsync();
        samples.Add(GC.GetTotalMemory(forceFullCollection: true));
        // The lowest level the full cache reached while running (a single sample can land in a burst of garbage)
        var baseline = samples.Count > 1 ? samples.Take(samples.Count - 1).Min() : samples[0];
        var peakGrowth = samples.Max() - baseline;

        _output.WriteLine($"Ran {watch.Elapsed.TotalSeconds:F0} s with {workers.Length - 1} workers: {counters.LoadCalls:N0} loads " +
                          $"({counters.HotLoadCalls:N0} hot), {counters.FactoryCalls:N0} factory calls ({counters.FactoryFailures:N0} failed), " +
                          $"{counters.GetCalls:N0} reads, {counters.Writes:N0} writes, {counters.Invalidations:N0} invalidations");
        _output.WriteLine($"Stats: {stats.Hits:N0} hits, {stats.Misses:N0} misses, {stats.Evictions:N0} evictions, {stats.Entries:N0} entries, " +
                          $"weight {stats.MemoryUsage:N0} (max seen {maxWeight:N0}, bound {WeightBound:N0}), max entries seen {maxEntries:N0}");
        _output.WriteLine($"Managed memory after full GCs (MB): {string.Join(", ", samples.Select(s => (s / (1024.0 * 1024)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}");

        Assert.Empty(counters.Unexpected);
        Assert.Equal(0, counters.WrongValues);

        // Stampede protection: a hot key (never invalidated) is loaded by one factory call at a time, and most
        // callers share a load instead of starting one
        Assert.Equal(0, counters.StampedeViolations);
        Assert.True(counters.HotFactoryCalls * 4 < counters.HotLoadCalls,
            $"{counters.HotFactoryCalls} hot factory calls for {counters.HotLoadCalls} hot loads: callers are not sharing loads");
        Assert.All(Enumerable.Range(0, HotKeys), i => Assert.False(cache.IsLoading(HotKey(i)), $"{HotKey(i)} still has a load registered"));

        // Counters: a miss is a factory call or a read that found nothing; every finished load or read counted one
        // hit or miss, and a failed load counted only for the caller that ran the factory (those that joined it
        // count nothing)
        Assert.Equal(counters.FactoryCalls + counters.GetMisses, stats.Misses);
        var counted = counters.LoadSucceeded + counters.FactoryFailures + counters.GetCalls + counters.TryGetHits;
        Assert.Equal(counted, stats.Hits + stats.Misses);

        // The weight bound holds once pending work is applied. While writers outpace eviction, the entries waiting in
        // the write buffer overshoot it for a moment (about 20 times over with every core writing, as here); a runaway
        // buffer would show here
        Assert.True(cache.Engine.TotalWeight <= WeightBound, $"weight {cache.Engine.TotalWeight} above the bound {WeightBound}");
        Assert.True(maxWeight <= 64 * WeightBound, $"weight reached {maxWeight}, {maxWeight / WeightBound}x the bound {WeightBound}");

        // No unbounded growth: once the load stops, managed memory is back near the lowest level the full cache
        // reached while running, and while running it never climbs without limit
        var finalGrowth = samples[^1] - baseline;
        Assert.True(finalGrowth < 16L * 1024 * 1024, $"managed memory grew by {finalGrowth / (1024 * 1024)} MB over the run");
        Assert.True(peakGrowth < 256L * 1024 * 1024, $"managed memory peaked {peakGrowth / (1024 * 1024)} MB above the baseline");
    }

    private static string HotKey(int i) => $"hot:{i}";

    private static string ColdKey(int i) => $"cold:{i}";

    /// <summary>A value that names its key: the first four bytes are the key's id (hot keys negative).</summary>
    private static byte[] ValueFor(int id, Random random)
    {
        var value = new byte[random.Next(16, MaxValueBytes)];
        BinaryPrimitives.WriteInt32LittleEndian(value, id);
        return value;
    }

    private static void Verify(byte[]? value, int id, Counters counters)
    {
        if (value != null && (value.Length < 4 || BinaryPrimitives.ReadInt32LittleEndian(value) != id))
        {
            Interlocked.Increment(ref counters.WrongValues);
        }
    }

    private static async Task WorkerAsync(InMemoryCacheManager cache, Counters counters, int seed, CancellationToken stop)
    {
        var random = new Random(seed);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var roll = random.Next(100);
                if (roll < 45)
                {
                    await LoadHotAsync(cache, counters, random.Next(HotKeys), random);
                }
                else if (roll < 75)
                {
                    await LoadColdAsync(cache, counters, random.Next(ColdKeys), random);
                }
                else if (roll < 85)
                {
                    var id = random.Next(ColdKeys);
                    Interlocked.Increment(ref counters.GetCalls);
                    var value = await cache.GetAsync<byte[]>(ColdKey(id));
                    if (value == null)
                    {
                        Interlocked.Increment(ref counters.GetMisses);
                    }

                    Verify(value, id, counters);
                }
                else if (roll < 95)
                {
                    var hot = random.Next(2) == 0;
                    var id = hot ? random.Next(HotKeys) : random.Next(ColdKeys);
                    if (cache.TryGetFast<byte[]>(hot ? HotKey(id) : ColdKey(id), out var value))
                    {
                        Interlocked.Increment(ref counters.TryGetHits);
                        Verify(value, hot ? -1 - id : id, counters);
                    }
                }
                else
                {
                    var id = random.Next(ColdKeys);
                    Interlocked.Increment(ref counters.Writes);
                    await cache.SetAsync(ColdKey(id), ValueFor(id, random), TimeSpan.FromSeconds(1));
                }
            }
            catch (SoakFactoryException)
            {
                // Expected: a cold factory failed, for the caller that ran it and for those that joined it
            }
            catch (Exception ex)
            {
                counters.Unexpected.Enqueue(ex);
                return;
            }
        }
    }

    private static async Task LoadHotAsync(InMemoryCacheManager cache, Counters counters, int index, Random random)
    {
        var id = -1 - index;
        Interlocked.Increment(ref counters.LoadCalls);
        Interlocked.Increment(ref counters.HotLoadCalls);
        var size = random.Next(16, MaxValueBytes);
        var value = await cache.GetOrCreateFastAsync(HotKey(index), "Soak.Hot", async () =>
        {
            Interlocked.Increment(ref counters.FactoryCalls);
            Interlocked.Increment(ref counters.HotFactoryCalls);
            if (Interlocked.Increment(ref counters.HotInFlight[index]) != 1)
            {
                Interlocked.Increment(ref counters.StampedeViolations);
            }

            try
            {
                await Task.Yield();
                Thread.SpinWait(500); // keep the load open long enough for other callers to arrive
                var bytes = new byte[size];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, id);
                return bytes;
            }
            finally
            {
                Interlocked.Decrement(ref counters.HotInFlight[index]);
            }
        }, HotPolicy);
        Interlocked.Increment(ref counters.LoadSucceeded);
        Verify(value, id, counters);
    }

    private static async Task LoadColdAsync(InMemoryCacheManager cache, Counters counters, int id, Random random)
    {
        Interlocked.Increment(ref counters.LoadCalls);
        var fail = random.Next(50) == 0;
        var size = random.Next(16, MaxValueBytes);
        var value = await cache.GetOrCreateFastAsync(ColdKey(id), "Soak.Cold", () =>
        {
            Interlocked.Increment(ref counters.FactoryCalls);
            if (fail)
            {
                Interlocked.Increment(ref counters.FactoryFailures);
                return Task.FromException<byte[]>(new SoakFactoryException());
            }

            var bytes = new byte[size];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, id);
            return Task.FromResult(bytes);
        }, ColdPolicies[id % ColdTagGroups]);
        Interlocked.Increment(ref counters.LoadSucceeded);
        Verify(value, id, counters);
    }

    private static async Task InvalidatorAsync(InMemoryCacheManager cache, Counters counters, CancellationToken stop)
    {
        var random = new Random(-1);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                switch (random.Next(10))
                {
                    case < 6:
                        await cache.InvalidateByKeysAsync(ColdKey(random.Next(ColdKeys)), ColdKey(random.Next(ColdKeys)));
                        break;
                    case < 9:
                        await cache.InvalidateByTagsAsync($"cold:{random.Next(ColdTagGroups)}");
                        break;
                    default:
                        await cache.InvalidateByTagPatternAsync($"cold:{random.Next(10)}*");
                        break;
                }

                Interlocked.Increment(ref counters.Invalidations);
                await Task.Delay(5, CancellationToken.None);
            }
            catch (Exception ex)
            {
                counters.Unexpected.Enqueue(ex);
                return;
            }
        }
    }
}

/// <summary>
/// A test that runs only when <c>METHODCACHE_SOAK_SECONDS</c> is set to a positive number of seconds, which is
/// also how long it runs.
/// </summary>
public sealed class SoakFactAttribute : FactAttribute
{
    /// <summary>The environment variable that enables soak tests and sets their duration in seconds.</summary>
    public const string DurationVariable = "METHODCACHE_SOAK_SECONDS";

    /// <summary>How long a soak test runs, or null when soak tests are off.</summary>
    public static TimeSpan? Duration =>
        int.TryParse(Environment.GetEnvironmentVariable(DurationVariable), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;

    /// <summary>Skips the test unless <see cref="DurationVariable"/> is set.</summary>
    public SoakFactAttribute()
    {
        if (Duration == null)
        {
            Skip = $"Soak test: set {DurationVariable} to run it (build/ci/jobs.sh soak)";
        }
    }
}
