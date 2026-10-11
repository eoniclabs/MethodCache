using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Properties;

/// <summary>
/// Properties of concurrent loads of the same key in <see cref="InMemoryCacheManager"/>: single flight (the
/// factory runs once however many callers miss together), one caller's cancellation does not cancel or fail the
/// others, and a failed load is not cached. Seeds and replay: see <see cref="PropertySettings"/>.
/// </summary>
public class ConcurrentLoadPropertyTests
{
    private static readonly string[] Keys = { "k0", "k1", "k2" };

    private static readonly CacheRuntimePolicy Policy = CacheRuntimePolicy.Empty("Concurrent.Load") with { Duration = TimeSpan.FromMinutes(1) };

    private static InMemoryCacheManager CreateCache(FakeTimeProvider time)
        => new(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }), time);

    /// <summary>A cache with a frozen clock, and how often each key's factory ran.</summary>
    private sealed class Harness
    {
        public InMemoryCacheManager Cache { get; } = CreateCache(new FakeTimeProvider());

        public int[] Calls { get; } = new int[Keys.Length];

        public void GetOrCreate(int key)
        {
            var value = Cache.GetOrCreateFastAsync(Keys[key], "M", () =>
            {
                Interlocked.Increment(ref Calls[key]);
                Thread.SpinWait(2_000); // let other callers arrive while the load runs, without needing the thread pool
                return Task.FromResult(100 + key);
            }, Policy).GetAwaiter().GetResult();

            Assert.Equal(100 + key, value);
        }

        public override string ToString()
            => string.Join(", ", Keys.Select((key, i) => $"{key}: {Calls[i]} call(s), {(Cache.ExistsAsync(key).AsTask().Result ? "cached" : "absent")}"));
    }

    /// <summary>
    /// Loads, reads and invalidations run in parallel must end in a state some sequential order of them also
    /// reaches: the same number of factory calls per key and the same keys cached. A sequential order calls a key's
    /// factory once, plus once after each invalidation that a later load follows; a stampede (two callers both
    /// running the factory) or an invalidation lost to a load that finished after it has no sequential equivalent.
    /// </summary>
    [Fact]
    public void ParallelLoadsAndInvalidations_AreLinearizable()
    {
        var genKey = Gen.Int[0, Keys.Length - 1];
        Check.SampleParallel(
            Gen.Const(() => new Harness()),
            new GenOperation<Harness>[]
            {
                genKey.Operation<Harness>(k => $"GetOrCreate({Keys[k]})", (h, k) => h.GetOrCreate(k)),
                genKey.Operation<Harness>(k => $"GetOrCreate({Keys[k]})", (h, k) => h.GetOrCreate(k)),
                genKey.Operation<Harness>(k => $"TryGetFast({Keys[k]})", (h, k) => h.Cache.TryGetFast<int>(Keys[k], out _)),
                genKey.Operation<Harness>(k => $"InvalidateByKeys({Keys[k]})", (h, k) => h.Cache.InvalidateByKeysAsync(Keys[k]).GetAwaiter().GetResult()),
            },
            equal: (a, b) => a.ToString() == b.ToString(),
            iter: PropertySettings.ConcurrentIterations,
            print: h => h.ToString());
    }

    /// <summary>
    /// Several callers miss the same key together; some of them cancel while the load runs (possibly the one that
    /// started it), then the factory finishes or throws. The factory sees its own caller's token, as generated
    /// code passes it on.
    /// </summary>
    [Fact]
    public async Task ConcurrentCallers_ShareOneLoad_CancellationStaysWithItsCaller_AndErrorsAreNotCached()
    {
        await Gen.Select(Gen.Int[2, 6], Gen.Int[0, 63], Gen.Bool).SampleAsync(async (callers, cancelMask, fails) =>
        {
            var time = new FakeTimeProvider();
            using var cache = CreateCache(time);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            var finished = 0;

            var sources = Enumerable.Range(0, callers).Select(_ => new CancellationTokenSource()).ToArray();
            var tasks = sources.Select(source =>
            {
                var token = source.Token;
                return cache.GetOrCreateFastAsync("key", "M", async () =>
                {
                    Interlocked.Increment(ref started);
                    await gate.Task.WaitAsync(token);
                    token.ThrowIfCancellationRequested(); // WaitAsync ignores the token once the gate is open
                    Interlocked.Increment(ref finished);
                    return fails ? throw new InvalidOperationException("factory failed") : 42;
                }, Policy, token);
            }).ToArray();

            var cancelled = Enumerable.Range(0, callers).Where(i => (cancelMask & (1 << i)) != 0).ToHashSet();
            foreach (var i in cancelled)
            {
                sources[i].Cancel();
            }

            gate.SetResult();

            for (var i = 0; i < callers; i++)
            {
                if (cancelled.Contains(i))
                {
                    // Cancelled while waiting, or (when it was retrying the load the first caller cancelled) it found
                    // the shared load already done and got its outcome; never a different value
                    var outcome = await Record.ExceptionAsync(() => tasks[i]);
                    Assert.True(
                        outcome is OperationCanceledException || (fails ? outcome is InvalidOperationException : outcome == null && tasks[i].Result == 42),
                        $"caller {i} cancelled, got {outcome?.GetType().Name ?? tasks[i].Result.ToString()}");
                }
                else if (fails)
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() => tasks[i]);
                }
                else
                {
                    Assert.Equal(42, await tasks[i]);
                }
            }

            var waiting = callers - cancelled.Count;
            var anyoneWaited = waiting > 0;
            if (fails && cancelled.Contains(0) && anyoneWaited)
            {
                // The load that the others joined was cancelled, so they load again; a failure is not cached, so a
                // caller that retries after another retry failed is a new caller and may run the factory too.
                Assert.InRange(finished, 1, waiting);
            }
            else
            {
                // One load ran to the end, shared by everyone who waited
                Assert.Equal(anyoneWaited ? 1 : 0, finished);
            }

            Assert.True(started - finished <= cancelled.Count, $"{started} loads started, {finished} finished, {cancelled.Count} caller(s) cancelled");

            // A value is cached for its Duration; a failure or a cancelled load caches nothing
            var calls = 0;
            var next = await cache.GetOrCreateFastAsync("key", "M", () => { calls++; return Task.FromResult(7); }, Policy);
            if (anyoneWaited && !fails)
            {
                Assert.Equal((42, 0), (next, calls));
                time.Advance(TimeSpan.FromMinutes(1));
                Assert.Equal(7, await cache.GetOrCreateFastAsync("key", "M", () => { calls++; return Task.FromResult(7); }, Policy));
                Assert.Equal(1, calls);
            }
            else
            {
                Assert.Equal((7, 1), (next, calls));
            }

            foreach (var source in sources)
            {
                source.Dispose();
            }
        }, iter: PropertySettings.ConcurrentIterations, print: t => $"{t.Item1} callers, cancelled mask {Convert.ToString(t.Item2 & ((1 << t.Item1) - 1), 2)} (bit 0 started the load), factory {(t.Item3 ? "throws" : "succeeds")}");
    }
}
