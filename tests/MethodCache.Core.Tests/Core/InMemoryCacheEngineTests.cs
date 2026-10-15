using FluentAssertions;
using MethodCache.Core.Runtime.Memory;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Runs on its own: the concurrency tests saturate the CPU, which starves timing-sensitive tests elsewhere.
/// </summary>
[CollectionDefinition(nameof(CpuIntensiveCollection), DisableParallelization = true)]
public sealed class CpuIntensiveCollection;

[Collection(nameof(CpuIntensiveCollection))]
public class InMemoryCacheEngineTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private InMemoryCacheManager CreateManager(Action<MethodCacheMemoryOptions>? configure = null)
    {
        var options = new MethodCacheMemoryOptions { EnableBackgroundCleanup = false };
        configure?.Invoke(options);
        return new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(options), _time);
    }

    private static CacheRuntimePolicy Policy(string method = "M", TimeSpan? duration = null) =>
        CacheRuntimePolicy.Empty(method) with { Duration = duration ?? OneMinute };

    private static CacheRuntimePolicy FailSafePolicy() => Policy() with { FailSafe = TimeSpan.FromHours(1) };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task AsyncHits_ReuseTheSameCompletedTask()
    {
        var manager = CreateManager();
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("value"), Policy());

        var first = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("other"), Policy());
        var second = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("other"), Policy());

        first.IsCompletedSuccessfully.Should().BeTrue();
        (await first).Should().Be("value");
        second.Should().BeSameAs(first, "hits must not allocate a new task each time");
    }

    [Fact]
    public async Task ConcurrentCallers_ExecuteFactoryOnce()
    {
        var manager = CreateManager();
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<string> Factory()
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return "value";
        }

        var callers = Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => manager.GetOrCreateFastAsync("key", "M", Factory, Policy())))
            .ToArray();

        // Release the load only once it is running, so the other callers join it or find its result.
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref calls) == 1));
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        calls.Should().Be(1);
        results.Should().OnlyContain(r => r == "value");
    }

    [Fact]
    public async Task FactoryFailure_PropagatesToWaiters_AndIsNotCached()
    {
        var manager = CreateManager();
        var calls = 0;

        Task<string> Failing()
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("boom");
        }

        var act = () => manager.GetOrCreateFastAsync("key", "M", Failing, Policy());
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("ok"), Policy())).Should().Be("ok");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Entry_ExpiresAfterDuration()
    {
        var manager = CreateManager();
        var calls = 0;
        Task<int> Factory() => Task.FromResult(++calls);

        (await manager.GetOrCreateFastAsync("key", "M", Factory, Policy())).Should().Be(1);
        _time.Advance(TimeSpan.FromSeconds(59));
        (await manager.GetOrCreateFastAsync("key", "M", Factory, Policy())).Should().Be(1);
        _time.Advance(TimeSpan.FromSeconds(2));
        (await manager.GetOrCreateFastAsync("key", "M", Factory, Policy())).Should().Be(2);
    }

    [Fact]
    public async Task SlidingExpiration_IsExtendedByReads_AndCappedByMaxExpiration()
    {
        var manager = CreateManager(o => o.MaxExpiration = TimeSpan.FromMinutes(3));
        // Without a Duration, only MaxExpiration bounds a sliding entry
        var policy = Policy() with { SlidingExpiration = TimeSpan.FromSeconds(30), Duration = null };
        var calls = 0;
        Task<int> Factory() => Task.FromResult(++calls);

        await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            (await manager.GetOrCreateFastAsync("key", "M", Factory, policy)).Should().Be(1);
        }

        // Absolute cap of three minutes from creation.
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        }

        calls.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task SlidingExpiration_ExpiresAnUnreadEntryAfterOneWindow()
    {
        var manager = CreateManager();
        var policy = Policy(duration: TimeSpan.FromMinutes(30)) with { SlidingExpiration = TimeSpan.FromMinutes(10) };

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(1), policy);
        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1));

        (await manager.ExistsAsync("key")).Should().BeFalse();
    }

    [Fact]
    public async Task Stats_ReportNoEvictions_WhenStatisticsAreDisabled()
    {
        var manager = CreateManager(o =>
        {
            o.MaxItems = 10;
            o.EnableStatistics = false;
        });

        for (var i = 0; i < 100; i++)
        {
            await manager.SetAsync($"k{i}", i, OneMinute);
        }

        (await manager.GetStatsAsync()).Evictions.Should().Be(0);
    }

    [Fact]
    public async Task RefreshAhead_ServesCurrentValue_AndRefreshesInBackground()
    {
        var manager = CreateManager();
        var policy = Policy() with { RefreshAhead = TimeSpan.FromSeconds(10) };
        var calls = 0;
        Task<int> Factory() => Task.FromResult(Interlocked.Increment(ref calls));

        await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        manager.TryGetFast<int>("key", out var fast).Should().BeTrue();
        fast.Should().Be(1);

        _time.Advance(TimeSpan.FromSeconds(55));

        // Decorators fall through to the loading path when a refresh is due...
        manager.TryGetFast<int>("key", out _).Should().BeFalse();
        // ...which returns the current value immediately and refreshes in the background.
        (await manager.GetOrCreateFastAsync("key", "M", Factory, policy)).Should().Be(1);

        await WaitUntilAsync(async () => await manager.GetAsync<int>("key") == 2);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task CacheWhen_RejectedRefresh_KeepsTheCurrentValue()
    {
        var manager = CreateManager();
        var policy = Policy() with { RefreshAhead = TimeSpan.FromSeconds(10), CacheWhen = v => (int)v! > 0 };
        var next = 5;
        var calls = 0;
        Task<int> Factory() { Interlocked.Increment(ref calls); return Task.FromResult(next); }

        await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        next = 0;
        _time.Advance(TimeSpan.FromSeconds(55));
        (await manager.GetOrCreateFastAsync("key", "M", Factory, policy)).Should().Be(5); // starts the refresh

        // Wait for the refresh to finish deciding whether to store, not just for its factory to run.
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref calls) == 2 && !manager.IsLoading("key")));
        (await manager.GetAsync<int>("key")).Should().Be(5);
    }

    [Fact]
    public async Task CacheWhen_RejectedLoad_KeepsTheFailSafeValue()
    {
        var manager = CreateManager();
        var policy = FailSafePolicy() with { CacheWhen = v => v is not "" };

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);
        _time.Advance(TimeSpan.FromMinutes(2));
        (await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(""), policy)).Should().BeEmpty();

        var result = await manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), policy);
        result.Should().Be("stale");
    }

    [Fact]
    public async Task FailSafeTimeout_ServesTheStaleValue_AndFinishesTheLoadInTheBackground()
    {
        var manager = CreateManager();
        var policy = FailSafePolicy() with { FailSafeTimeout = TimeSpan.FromMilliseconds(200) };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);
        _time.Advance(TimeSpan.FromMinutes(2));

        var call = manager.GetOrCreateFastAsync("key", "M", () => gate.Task, policy);
        var joined = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("unused"), policy);
        call.IsCompleted.Should().BeFalse("the timeout has not passed yet");
        joined.IsCompleted.Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(200));
        (await call).Should().Be("stale");
        (await joined).Should().Be("stale", "a caller that joined the slow load gets the same timeout");

        // Callers meanwhile get the stale value without starting another load.
        var started = false;
        (await manager.GetOrCreateFastAsync("key", "M", () => { started = true; return Task.FromResult("other"); }, policy)).Should().Be("stale");
        started.Should().BeFalse();

        gate.SetResult("fresh");
        await WaitUntilAsync(async () => await manager.GetAsync<string>("key") == "fresh");
    }

    [Fact]
    public async Task FailSafeTimeout_WaitsForTheLoad_WhenThereIsNoStaleValue()
    {
        var manager = CreateManager();
        var policy = FailSafePolicy() with { FailSafeTimeout = TimeSpan.FromMilliseconds(200) };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = manager.GetOrCreateFastAsync("key", "M", () => gate.Task, policy);
        _time.Advance(TimeSpan.FromSeconds(5));
        call.IsCompleted.Should().BeFalse();

        gate.SetResult("first");
        (await call).Should().Be("first");
    }

    [Fact]
    public async Task FailSafe_Zero_TurnsItOff()
    {
        var manager = CreateManager();
        var policy = Policy() with { FailSafe = TimeSpan.Zero };

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);
        _time.Advance(TimeSpan.FromMinutes(2));

        await FluentActions.Awaiting(() => manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), policy))
            .Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task FailSafeRetryAfter_SetsHowLongTheStaleValueIsReused()
    {
        var manager = CreateManager();
        var policy = FailSafePolicy() with { FailSafeRetryAfter = TimeSpan.FromSeconds(5) };

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);
        _time.Advance(TimeSpan.FromMinutes(2));
        (await manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), policy)).Should().Be("stale");

        _time.Advance(TimeSpan.FromSeconds(6));
        (await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("fresh"), policy)).Should().Be("fresh");
    }

    [Fact]
    public async Task FailSafe_ReturnsStaleValue_WhenFactoryThrows()
    {
        var manager = CreateManager();

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), FailSafePolicy());
        _time.Advance(TimeSpan.FromMinutes(2));

        var result = await manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), FailSafePolicy());
        result.Should().Be("stale");

        // Throttled: the stale value is served without retrying the factory for a while.
        var retried = false;
        (await manager.GetOrCreateFastAsync("key", "M", () => { retried = true; return Task.FromResult("fresh"); }, FailSafePolicy()))
            .Should().Be("stale");
        retried.Should().BeFalse();
    }

    [Fact]
    public async Task FailSafe_LeavesAWriteMadeDuringTheFailedLoadUntouched()
    {
        var manager = CreateManager();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), FailSafePolicy());
        _time.Advance(TimeSpan.FromMinutes(2));

        var load = manager.GetOrCreateFastAsync("key", "M", () => gate.Task, FailSafePolicy());
        await manager.SetAsync("key", "newer", TimeSpan.FromMinutes(10));
        gate.SetException(new TimeoutException());

        await FluentActions.Awaiting(() => load).Should().ThrowAsync<TimeoutException>();

        // The fail-safe throttle must not have shortened the newer entry's lifetime.
        _time.Advance(TimeSpan.FromMinutes(5));
        (await manager.GetAsync<string>("key")).Should().Be("newer");
    }

    [Fact]
    public async Task FailSafe_AppliesToCallersJoiningAFailedBackgroundRefresh()
    {
        var manager = CreateManager();
        var policy = FailSafePolicy() with { RefreshAhead = TimeSpan.FromSeconds(10) };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);
        _time.Advance(TimeSpan.FromSeconds(55));
        (await manager.GetOrCreateFastAsync("key", "M", () => gate.Task, policy)).Should().Be("stale"); // starts the refresh

        _time.Advance(TimeSpan.FromSeconds(10)); // now logically expired, the refresh is still running
        var joined = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("unused"), policy);
        gate.SetException(new TimeoutException());

        (await joined).Should().Be("stale");

        // Throttled like the foreground path: the next caller is served the stale value without a retry.
        var retried = false;
        (await manager.GetOrCreateFastAsync("key", "M", () => { retried = true; return Task.FromResult("fresh"); }, policy))
            .Should().Be("stale");
        retried.Should().BeFalse();
    }

    [Fact]
    public async Task SetAsync_DuringALoad_IsNotOverwrittenByTheOlderResult()
    {
        var manager = CreateManager();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var load = manager.GetOrCreateFastAsync("key", "M", () => gate.Task, Policy());
        await manager.SetAsync("key", "newer", OneMinute);
        gate.SetResult("older");

        (await load).Should().Be("older");
        (await manager.GetAsync<string>("key")).Should().Be("newer");
    }

    [Fact]
    public async Task TryReplace_StoresNothing_WhenTheExpectedEntryWasReplaced()
    {
        var manager = CreateManager();
        var engine = manager.Engine;
        engine.ForegroundMaintenance = true;

        await manager.SetAsync("key", "refreshed", OneMinute);
        engine.TryPeek("key", out var refreshed).Should().BeTrue();
        await manager.SetAsync("key", "newer", OneMinute);

        var expiresAt = engine.Now + OneMinute.Ticks * 100;
        var late = new ValueNode<string>("key", "late")
        {
            LogicalExpiresAt = expiresAt,
            PhysicalExpiresAt = expiresAt,
            AbsoluteExpiresAt = expiresAt,
            Tags = new[] { "late-tag" }
        };

        engine.TryReplace(refreshed, late).Should().BeFalse();
        engine.Maintain();

        (await manager.GetAsync<string>("key")).Should().Be("newer");
        engine.Tags.Should().NotContain("late-tag");
    }

    [Fact]
    public async Task BackgroundRefresh_ReturningNull_RemovesTheRefreshedEntry()
    {
        var manager = CreateManager(o => o.CacheNullValues = false);
        var policy = Policy() with { RefreshAhead = TimeSpan.FromSeconds(10) };

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult<string?>("value"), policy);
        _time.Advance(TimeSpan.FromSeconds(55));
        (await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult<string?>(null), policy)).Should().Be("value");

        await WaitUntilAsync(async () => !await manager.ExistsAsync("key"));
    }

    [Fact]
    public async Task CallersAfterAnInvalidation_StartAFreshLoad_InsteadOfJoiningTheOldOne()
    {
        var manager = CreateManager();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var oldLoad = manager.GetOrCreateFastAsync("key", "M", () => gate.Task, Policy());
        await manager.RemoveAsync("key");

        var freshLoad = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("fresh"), Policy());
        freshLoad.IsCompleted.Should().BeTrue("the fresh load must not wait for the invalidated one");
        (await freshLoad).Should().Be("fresh");

        gate.SetResult("old");
        (await oldLoad).Should().Be("old");
        (await manager.GetAsync<string>("key")).Should().Be("fresh");

        // The old load's cleanup must leave nothing behind that later callers would join.
        await manager.RemoveAsync("key");
        (await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("third"), Policy())).Should().Be("third");
    }

    [Fact]
    public async Task Jitter_NeverExtendsBeyondMaxExpiration()
    {
        var manager = CreateManager(o =>
        {
            o.MaxExpiration = OneMinute;
            o.JitterMaxDuration = TimeSpan.FromSeconds(30);
        });

        for (var i = 0; i < 50; i++)
        {
            await manager.SetAsync($"k{i}", i, OneMinute);
        }

        _time.Advance(OneMinute + TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 50; i++)
        {
            (await manager.ExistsAsync($"k{i}")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task WithoutFailSafe_FactoryFailureAfterExpiry_Throws()
    {
        var manager = CreateManager();

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), Policy());
        _time.Advance(TimeSpan.FromMinutes(2));

        var act = () => manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), Policy());
        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public async Task NullResults_AreCachedOnlyWhenEnabled(bool cacheNulls, int expectedCalls)
    {
        var manager = CreateManager(o => o.CacheNullValues = cacheNulls);
        var calls = 0;
        Task<string?> Factory()
        {
            calls++;
            return Task.FromResult<string?>(null);
        }

        for (var i = 0; i < 3; i++)
        {
            (await manager.GetOrCreateFastAsync("key", "M", Factory, Policy())).Should().BeNull();
        }

        calls.Should().Be(expectedCalls);
    }

    [Fact]
    public async Task InvalidateByTags_RemovesTaggedEntries()
    {
        var manager = CreateManager();
        var tagged = Policy() with { Tags = new[] { "users", "user:1" } };

        await manager.GetOrCreateFastAsync("a", "M", () => Task.FromResult(1), tagged);
        await manager.GetOrCreateFastAsync("b", "M", () => Task.FromResult(2), Policy());

        await manager.InvalidateByTagsAsync("user:1");

        (await manager.ExistsAsync("a")).Should().BeFalse();
        (await manager.ExistsAsync("b")).Should().BeTrue();
    }

    [Fact]
    public async Task InvalidationDuringLoad_PreventsStaleResultFromBeingStored()
    {
        var manager = CreateManager();
        var tagged = Policy() with { Tags = new[] { "users" } };
        var gate = new TaskCompletionSource<int>();

        var loading = manager.GetOrCreateFastAsync("a", "M", () => gate.Task, tagged);
        await manager.InvalidateByTagsAsync("users");
        gate.SetResult(1);

        (await loading).Should().Be(1);
        (await manager.ExistsAsync("a")).Should().BeFalse();
    }

    [Fact]
    public async Task InvalidateByKeyDuringLoad_PreventsStaleResultFromBeingStored()
    {
        var manager = CreateManager();
        var gate = new TaskCompletionSource<int>();

        var loading = manager.GetOrCreateFastAsync("a", "M", () => gate.Task, Policy());
        await manager.InvalidateByKeysAsync("a");
        gate.SetResult(1);

        await loading;
        (await manager.ExistsAsync("a")).Should().BeFalse();
    }

    [Theory]
    [InlineData("user:*", new[] { "user:1", "user:12", "user:1:profile" })]
    [InlineData("user:?", new[] { "user:1" })]
    [InlineData("*:profile", new[] { "user:1:profile" })]
    public async Task InvalidateByTagPattern_UsesWildcards(string pattern, string[] expectedRemoved)
    {
        var manager = CreateManager();
        var tags = new[] { "user:1", "user:12", "user:1:profile", "order:1" };
        foreach (var tag in tags)
        {
            await manager.GetOrCreateFastAsync(tag, "M", () => Task.FromResult(tag), Policy() with { Tags = new[] { tag } });
        }

        await manager.InvalidateByTagPatternAsync(pattern);

        foreach (var tag in tags)
        {
            (await manager.ExistsAsync(tag)).Should().Be(!expectedRemoved.Contains(tag), tag);
        }
    }

    [Theory]
    [InlineData((int)MemoryCacheEvictionPolicy.TinyLfu)]
    [InlineData((int)MemoryCacheEvictionPolicy.Lru)]
    [InlineData((int)MemoryCacheEvictionPolicy.Fifo)]
    public async Task Size_IsBoundedByMaxItems(int policyValue)
    {
        var policy = (MemoryCacheEvictionPolicy)policyValue;
        var manager = CreateManager(o =>
        {
            o.MaxItems = 100;
            o.EvictionPolicy = policy;
        });

        for (var i = 0; i < 1000; i++)
        {
            await manager.SetAsync($"k{i}", i, OneMinute);
        }

        var stats = await manager.GetStatsAsync();
        stats.Entries.Should().BeLessThanOrEqualTo(100);
        stats.Evictions.Should().BeGreaterThanOrEqualTo(900);
    }

    [Fact]
    public async Task Statistics_CountHitsOnTheFastPath()
    {
        // Generated decorators read hits through TryGetFastTask, so skipping them there left the hit ratio near zero
        var manager = CreateManager();
        await manager.SetAsync("k", "value", OneMinute);

        manager.TryGetFastTask<string>("k", out _).Should().BeTrue();
        manager.TryGetFast<string>("k", out _).Should().BeTrue();

        (await manager.GetStatsAsync()).Hits.Should().Be(2);
    }

    [Fact]
    public async Task TinyLfu_KeepsFrequentlyUsedEntries_DuringAScan()
    {
        var manager = CreateManager(o => o.MaxItems = 1000);
        manager.Engine.ForegroundMaintenance = true; // record every access so the outcome does not depend on thread pool timing

        // A hot working set, read many times.
        for (var round = 0; round < 10; round++)
        {
            for (var i = 0; i < 500; i++)
            {
                await manager.GetOrCreateFastAsync($"hot{i}", "M", () => Task.FromResult(i), Policy());
            }
        }

        // A one-off scan much larger than the cache.
        for (var i = 0; i < 20_000; i++)
        {
            await manager.SetAsync($"scan{i}", i, OneMinute);
        }

        var survivors = 0;
        for (var i = 0; i < 500; i++)
        {
            if (await manager.ExistsAsync($"hot{i}"))
            {
                survivors++;
            }
        }

        survivors.Should().BeGreaterThan(450, "a frequency-aware policy should not let a scan flush the hot set");
    }

    [Fact]
    public async Task Weigher_BoundsTotalWeight()
    {
        var manager = CreateManager(o =>
        {
            o.Weigher = (_, value) => ((string)value!).Length;
            o.MaxMemoryBytes = 1000;
        });

        for (var i = 0; i < 100; i++)
        {
            await manager.SetAsync($"k{i}", new string('x', 100), OneMinute);
        }

        var stats = await manager.GetStatsAsync();
        stats.MemoryUsage.Should().BeLessThanOrEqualTo(1000);
        stats.Entries.Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task Weigher_RejectsAnEntryLargerThanTheWholeCache_WithoutFlushingOthers()
    {
        var manager = CreateManager(o =>
        {
            o.Weigher = (_, value) => ((string)value!).Length;
            o.MaxMemoryBytes = 1000;
        });

        for (var i = 0; i < 5; i++)
        {
            await manager.SetAsync($"small{i}", new string('x', 100), OneMinute);
        }

        await manager.SetAsync("huge", new string('x', 5000), OneMinute);

        (await manager.ExistsAsync("huge")).Should().BeFalse();
        for (var i = 0; i < 5; i++)
        {
            (await manager.ExistsAsync($"small{i}")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task MaxValueDurations_AreCappedByMaxExpiration_InsteadOfOverflowing()
    {
        var manager = CreateManager(o => o.MaxExpiration = TimeSpan.FromDays(365));
        var calls = 0;
        var policy = Policy(duration: TimeSpan.MaxValue);

        await manager.GetOrCreateFastAsync("a", "M", () => Task.FromResult(++calls), policy);
        await manager.GetOrCreateFastAsync("a", "M", () => Task.FromResult(++calls), policy);
        await manager.SetAsync("b", "value", TimeSpan.MaxValue);
        _time.Advance(TimeSpan.FromDays(300));

        calls.Should().Be(1);
        (await manager.GetAsync<string>("b")).Should().Be("value");
        (await manager.ExistsAsync("a")).Should().BeTrue();

        _time.Advance(TimeSpan.FromDays(66));
        (await manager.ExistsAsync("a")).Should().BeFalse();
    }

    [Fact]
    public async Task HugeMaxItems_DoesNotAllocateUpFront()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var manager = CreateManager(o => o.MaxItems = int.MaxValue);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        await manager.SetAsync("k", "v", OneMinute);
        (await manager.GetAsync<string>("k")).Should().Be("v");
        allocated.Should().BeLessThan(1_000_000);
    }

    [Fact]
    public async Task ClearAsync_ResetsEvictionCount()
    {
        var manager = CreateManager(o => o.MaxItems = 10);
        for (var i = 0; i < 100; i++)
        {
            await manager.SetAsync($"k{i}", i, OneMinute);
        }

        (await manager.GetStatsAsync()).Evictions.Should().BeGreaterThan(0);
        await manager.ClearAsync();
        (await manager.GetStatsAsync()).Evictions.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentTagChurn_KeepsEveryLiveEntryInvalidatable()
    {
        var manager = CreateManager(o => o.MaxItems = 100_000);
        var policy = Policy() with { Tags = new[] { "t" } };

        // Entries come and go under one tag so its node set repeatedly empties while others are being added.
        await Task.WhenAll(Enumerable.Range(0, Environment.ProcessorCount).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                var key = $"w{worker}:{i}";
                await manager.GetOrCreateFastAsync(key, "M", () => Task.FromResult(i), policy);
                await manager.RemoveAsync(key);
            }

            await manager.GetOrCreateFastAsync($"w{worker}:last", "M", () => Task.FromResult(-1), policy);
        })));

        await manager.InvalidateByTagsAsync("t");

        for (var worker = 0; worker < Environment.ProcessorCount; worker++)
        {
            (await manager.ExistsAsync($"w{worker}:last")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Jitter_SpreadsExpiration_WithinBounds()
    {
        var manager = CreateManager(o => o.JitterMaxDuration = TimeSpan.FromSeconds(30));
        for (var i = 0; i < 200; i++)
        {
            await manager.GetOrCreateFastAsync($"k{i}", "M", () => Task.FromResult(i), Policy());
        }

        _time.Advance(TimeSpan.FromSeconds(75));
        var alive = 0;
        for (var i = 0; i < 200; i++)
        {
            if (await manager.ExistsAsync($"k{i}"))
            {
                alive++;
            }
        }

        alive.Should().BeInRange(1, 199);

        _time.Advance(TimeSpan.FromSeconds(16));
        for (var i = 0; i < 200; i++)
        {
            (await manager.ExistsAsync($"k{i}")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task SameKeyWithDifferentType_IsAMiss_NotAnError()
    {
        var manager = CreateManager();
        await manager.SetAsync("key", "text", OneMinute);

        manager.TryGetFast<int>("key", out _).Should().BeFalse();
        (await manager.GetAsync<string>("key")).Should().Be("text");
    }

    [Fact]
    public async Task Statistics_CountHitsAndMisses()
    {
        var manager = CreateManager();
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(1), Policy());
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(1), Policy());
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(1), Policy());

        var stats = await manager.GetStatsAsync();
        stats.Misses.Should().Be(1);
        stats.Hits.Should().Be(2);
    }

    [Theory]
    [InlineData((int)MemoryCacheEvictionPolicy.TinyLfu)]
    [InlineData((int)MemoryCacheEvictionPolicy.Lru)]
    public void ConcurrentWriters_CannotOutrunEviction(int policyValue)
    {
        var policy = (MemoryCacheEvictionPolicy)policyValue;
        const int maxItems = 100;
        var writers = Math.Max(4, Environment.ProcessorCount * 2);
        var manager = CreateManager(o =>
        {
            o.MaxItems = maxItems;
            o.EvictionPolicy = policy;
        });
        var engine = manager.Engine;

        // Tracked weight: at most one entry over the bound at any moment. Stored entries: also the inserts waiting
        // for maintenance, which back-pressure caps at the write buffer limit plus one per writer, and one entry
        // per writer stored but not yet queued.
        var maxCount = maxItems + 1 + MemoryCacheEngine.WriteBufferMaximum + (2 * writers);
        long maxWeightSeen = 0;
        long maxCountSeen = 0;
        using var start = new Barrier(writers);

        var threads = Enumerable.Range(0, writers).Select(writer => new Thread(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 20_000; i++)
            {
                manager.SetAsync($"w{writer}:{i}", i, OneMinute).GetAwaiter().GetResult();
                InterlockedMax(ref maxWeightSeen, engine.TotalWeight);
                if ((i & 255) == 0)
                {
                    InterlockedMax(ref maxCountSeen, engine.Count);
                }
            }
        })).ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        maxWeightSeen.Should().BeLessThanOrEqualTo(maxItems + 1);
        maxCountSeen.Should().BeLessThanOrEqualTo(maxCount);
        engine.Maintain();
        engine.Count.Should().BeLessThanOrEqualTo(maxItems);

        static void InterlockedMax(ref long target, long value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var seen = Interlocked.CompareExchange(ref target, value, current);
                if (seen == current)
                {
                    return;
                }

                current = seen;
            }
        }
    }

    [Fact]
    public async Task ConcurrentMixedOperations_KeepInvariants()
    {
        var manager = new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(new MethodCacheMemoryOptions { MaxItems = 500 }));
        var tagged = Policy() with { Tags = new[] { "t1", "t2" } };

        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(worker => Task.Run(async () =>
        {
            var random = new Random(worker);
            for (var i = 0; i < 5_000; i++)
            {
                var key = $"k{random.Next(2_000)}";
                switch (random.Next(10))
                {
                    case < 6:
                        await manager.GetOrCreateFastAsync(key, "M", () => Task.FromResult(key), tagged);
                        break;
                    case 6:
                        await manager.SetAsync(key, key, OneMinute);
                        break;
                    case 7:
                        await manager.RemoveAsync(key);
                        break;
                    case 8:
                        manager.TryGetFast<string>(key, out var value);
                        if (value != null)
                        {
                            value.Should().Be(key);
                        }

                        break;
                    default:
                        if (random.Next(100) == 0)
                        {
                            await manager.InvalidateByTagsAsync("t1");
                        }

                        break;
                }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        // A maintenance pass on another thread (a read-buffer drain) can still be applying the last writes; the
        // bound is exact once they are applied
        manager.Engine.Maintain();
        var stats = await manager.GetStatsAsync();
        stats.Entries.Should().BeLessThanOrEqualTo(500);
    }
}

public class FrequencySketchTests
{
    [Fact]
    public void Increment_RaisesFrequency_UpToFifteen()
    {
        var sketch = new FrequencySketch(512);
        var hash = "item".GetHashCode();

        sketch.Frequency(hash).Should().Be(0);
        for (var i = 0; i < 5; i++)
        {
            sketch.Increment(hash);
        }

        sketch.Frequency(hash).Should().Be(5);

        for (var i = 0; i < 20; i++)
        {
            sketch.Increment(hash);
        }

        sketch.Frequency(hash).Should().Be(15);
    }

    [Fact]
    public void Reset_HalvesCounters()
    {
        var sketch = new FrequencySketch(64);
        var hot = "hot".GetHashCode();
        for (var i = 0; i < 10; i++)
        {
            sketch.Increment(hot);
        }

        // Fill the sample to trigger aging.
        for (var i = 0; i < sketch.SampleSize; i++)
        {
            sketch.Increment(i);
        }

        sketch.Frequency(hot).Should().BeLessThan(10, "aging halves every counter");
    }
}

public class TimerWheelTests
{
    [Theory]
    [InlineData(10)]          // < 1 second
    [InlineData(5_000)]       // seconds
    [InlineData(600_000)]     // minutes
    [InlineData(7_200_000)]   // hours
    [InlineData(200_000_000)] // days
    public void ExpiresNode_AfterItsDeadline(long delayMilliseconds)
    {
        var start = 1_000_000_000L;
        var wheel = new TimerWheel(start);
        var node = new ValueNode<int>("k", 1) { PhysicalExpiresAt = start + delayMilliseconds * 1_000_000 };
        wheel.Schedule(node);

        var expired = new List<Node>();
        wheel.Advance(start + (delayMilliseconds - 1) * 1_000_000, n => { expired.Add(n); return true; });
        expired.Should().BeEmpty();

        wheel.Advance(start + delayMilliseconds * 1_000_000 + 2_000_000_000L * 64, n => { expired.Add(n); return true; });
        expired.Should().ContainSingle().Which.Should().BeSameAs(node);
    }

    [Theory]
    [InlineData(0.1, 7)]
    [InlineData(3.3, 7)]
    [InlineData(6.4, 7)]
    [InlineData(1, 14)]
    [InlineData(5, 30)]
    public void LongLifetimes_ExpireWithinAnHourOfTheirDeadline(double startDays, double lifetimeDays)
    {
        // Advanced minute by minute, as the cleanup timer would, from various offsets against the coarse bucket boundaries.
        const long minute = 60_000_000_000L;
        var start = (long)(startDays * 1440) * minute;
        var deadline = start + (long)(lifetimeDays * 1440) * minute;
        var wheel = new TimerWheel(start);
        var node = new ValueNode<int>("k", 1) { PhysicalExpiresAt = deadline };
        wheel.Schedule(node);

        long? expiredAt = null;
        for (var now = start + minute; expiredAt == null && now <= deadline + 1440 * minute; now += minute)
        {
            var current = now;
            wheel.Advance(now, _ => { expiredAt = current; return true; });
        }

        expiredAt.Should().NotBeNull();
        expiredAt!.Value.Should().BeGreaterThanOrEqualTo(deadline);
        (expiredAt.Value - deadline).Should().BeLessThan(60 * minute);
    }

    [Fact]
    public void Deschedule_PreventsExpiry()
    {
        var wheel = new TimerWheel(0);
        var node = new ValueNode<int>("k", 1) { PhysicalExpiresAt = 1_000_000 };
        wheel.Schedule(node);
        wheel.Deschedule(node);

        var expired = false;
        wheel.Advance(10_000_000_000L, _ => expired = true);
        expired.Should().BeFalse();
    }
}

public class WildcardPatternTests
{
    [Theory]
    [InlineData("user:*", "user:1", true)]
    [InlineData("user:*", "user:", true)]
    [InlineData("user:*", "order:1", false)]
    [InlineData("user:??", "user:12", true)]
    [InlineData("user:??", "user:123", false)]
    [InlineData("*:profile", "user:1:profile", true)]
    [InlineData("a*b*c", "axxbyyc", true)]
    [InlineData("a*b*c", "axxbyy", false)]
    [InlineData("user.[1]", "user.[1]", true)]
    [InlineData("user.[1]", "userx[1]", false)]
    public void Matches(string pattern, string text, bool expected)
    {
        WildcardPattern.IsMatch(pattern, text).Should().Be(expected);
    }
}
