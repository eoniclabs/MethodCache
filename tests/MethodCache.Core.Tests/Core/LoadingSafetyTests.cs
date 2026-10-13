using System.Runtime.CompilerServices;
using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Loads that must not go wrong in edge cases: fail-safe after an invalidation, a factory asking for its own key,
/// a refresh window as long as the entry, and a manager nobody disposed.
/// </summary>
[Collection(nameof(CpuIntensiveCollection))]
public class LoadingSafetyTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private InMemoryCacheManager CreateManager(Action<MethodCacheMemoryOptions>? configure = null)
    {
        var options = new MethodCacheMemoryOptions { EnableBackgroundCleanup = false };
        configure?.Invoke(options);
        return new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(options), _time);
    }

    private static CacheRuntimePolicy Policy(TimeSpan? duration = null) =>
        CacheRuntimePolicy.Empty("M") with { Duration = duration ?? OneMinute };

    private static CacheRuntimePolicy FailSafePolicy() => Policy() with { FailSafe = TimeSpan.FromHours(1) };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    // ---------- Fail-safe serves a value invalidated during the load ----------

    [Fact]
    public async Task FailSafeTimeout_JoinedCaller_DoesNotGetAValueInvalidatedDuringTheLoad()
    {
        var manager = CreateManager();
        var policy = FailSafePolicy() with { FailSafeTimeout = TimeSpan.FromMilliseconds(200), Tags = new[] { "t" } };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("old"), policy);
        _time.Advance(TimeSpan.FromMinutes(2)); // logically expired, inside the fail-safe window

        var leader = manager.GetOrCreateFastAsync("key", "M", () => gate.Task, policy);
        var joined = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("unused"), policy);

        await manager.InvalidateByTagsAsync("t");
        _time.Advance(TimeSpan.FromMilliseconds(200)); // both callers reach the fail-safe timeout

        gate.SetResult("new");
        (await leader).Should().Be("new");
        (await joined).Should().Be("new", "the stale value was invalidated, so it must not be served");
    }

    [Fact]
    public async Task FailedBackgroundRefresh_JoinedCaller_DoesNotGetAValueInvalidatedDuringTheRefresh()
    {
        var manager = CreateManager();
        var policy = FailSafePolicy() with { RefreshAhead = TimeSpan.FromSeconds(10) };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("old"), policy);
        _time.Advance(TimeSpan.FromSeconds(55));
        (await manager.GetOrCreateFastAsync("key", "M", () => gate.Task, policy)).Should().Be("old"); // starts the refresh

        _time.Advance(TimeSpan.FromSeconds(10)); // logically expired while the refresh runs
        var joined = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("unused"), policy);
        await manager.InvalidateByKeysAsync("key");
        gate.SetException(new TimeoutException("dependency down"));

        var result = async () => await joined;
        await result.Should().ThrowAsync<TimeoutException>("the only fallback value was invalidated");
    }

    // ---------- A factory that reads its own key hangs that key ----------

    [Fact]
    public async Task GetOrCreateFastAsync_FactoryRequestsItsOwnKey_FailsInsteadOfHangingTheKey()
    {
        var manager = CreateManager();
        Task<string> Reentrant() => manager.GetOrCreateFastAsync("key", "M", Reentrant, Policy());

        var call = manager.GetOrCreateFastAsync("key", "M", Reentrant, Policy());
        var outcome = async () => await call.WaitAsync(TimeSpan.FromSeconds(2));
        await outcome.Should().ThrowAsync<InvalidOperationException>("a re-entrant load can never complete");

        var later = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("ok"), Policy());
        (await later.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("ok", "later callers of the key must not hang");
    }

    // ---------- The maintenance timer roots the engine and the creator's ExecutionContext ----------

    [Fact]
    public void InMemoryCacheManager_NeverDisposed_DoesNotKeepItsValuesAlive()
    {
        var weak = Fill();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        weak.TryGetTarget(out _).Should().BeFalse("nothing references the manager any more");

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference<object> Fill()
        {
            var manager = new InMemoryCacheManager(new NullCacheMetricsProvider()); // background cleanup on (default)
            var value = new object();
            manager.SetAsync("k", value, TimeSpan.FromHours(1)).GetAwaiter().GetResult();
            return new WeakReference<object>(value);
        }
    }

    private static readonly AsyncLocal<object?> Ambient = new();

    [Fact]
    public void InMemoryCacheManager_CreatedInsideARequest_DoesNotKeepTheRequestsAmbientState()
    {
        var (manager, weak) = Create();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        weak.TryGetTarget(out _).Should().BeFalse("the first resolver's AsyncLocal values must not live as long as the cache");
        manager.Dispose();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (InMemoryCacheManager, WeakReference<object>) Create()
        {
            // Run like the first request that resolves the singleton: with that request's ambient state
            InMemoryCacheManager? created = null;
            WeakReference<object>? reference = null;
            var thread = new Thread(() =>
            {
                var requestState = new object();
                Ambient.Value = requestState;
                created = new InMemoryCacheManager(new NullCacheMetricsProvider());
                reference = new WeakReference<object>(requestState);
            });
            thread.Start();
            thread.Join();
            return (created!, reference!);
        }
    }

    // ---------- RefreshAhead >= Duration refreshes on every hit ----------

    [Fact]
    public async Task GetOrCreateFastAsync_RefreshAheadLongerThanDuration_DoesNotReloadOnEveryHit()
    {
        var manager = CreateManager();
        var policy = Policy(TimeSpan.FromMinutes(1)) with { RefreshAhead = TimeSpan.FromMinutes(5) };
        var calls = 0;
        Task<int> Load() => Task.FromResult(Interlocked.Increment(ref calls));

        await manager.GetOrCreateFastAsync("key", "M", Load, policy);
        for (var i = 0; i < 5; i++)
        {
            await manager.GetOrCreateFastAsync("key", "M", Load, policy);
            await WaitUntilAsync(() => !manager.IsLoading("key"));
        }

        calls.Should().BeLessThan(3, "six reads within a few milliseconds of a one-minute entry");
    }
}
