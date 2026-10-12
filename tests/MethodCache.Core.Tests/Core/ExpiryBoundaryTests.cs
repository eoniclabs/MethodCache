using FluentAssertions;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Pins every time-based rule to its exact boundary with a fake clock: one tick before it still holds, at it the
/// rule applies.
/// </summary>
public class ExpiryBoundaryTests
{
    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private InMemoryCacheManager CreateManager(Action<MethodCacheMemoryOptions>? configure = null, DistributedCacheLayer? distributed = null)
    {
        var options = new MethodCacheMemoryOptions { EnableBackgroundCleanup = false };
        configure?.Invoke(options);
        return new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(options), _time, distributed);
    }

    private DistributedCacheLayer CreateLayer(IDistributedCache cache, Action<DistributedCacheOptions>? configure = null)
    {
        var options = new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero };
        configure?.Invoke(options);
        return new DistributedCacheLayer(cache, new JsonCacheSerializer(), options, backplane: null, _time);
    }

    private static CacheRuntimePolicy Policy(TimeSpan duration) => CacheRuntimePolicy.Empty("M") with { Duration = duration };

    private sealed class Counter
    {
        public int Calls;

        public Task<int> Next() => Task.FromResult(++Calls);
    }

    [Fact]
    public async Task Duration_IsFreshOneTickBeforeTheBoundary_AndExpiredExactlyAtIt()
    {
        var manager = CreateManager();
        var policy = Policy(TimeSpan.FromSeconds(30));
        var counter = new Counter();

        await manager.GetOrCreateFastAsync("key", "M", counter.Next, policy);

        _time.Advance(TimeSpan.FromSeconds(30) - OneTick);
        (await manager.GetOrCreateFastAsync("key", "M", counter.Next, policy)).Should().Be(1);
        manager.TryGetFast<int>("key", out _).Should().BeTrue();

        _time.Advance(OneTick);
        manager.TryGetFast<int>("key", out _).Should().BeFalse("the entry expires exactly at its Duration");
        (await manager.GetOrCreateFastAsync("key", "M", counter.Next, policy)).Should().Be(2);
    }

    [Fact]
    public async Task ExplicitSetDuration_ExpiresExactlyAtItsOwnLifetime()
    {
        var manager = CreateManager(o => o.DefaultExpiration = TimeSpan.FromHours(1));

        await manager.SetAsync("key", "value", TimeSpan.FromSeconds(5));

        _time.Advance(TimeSpan.FromSeconds(5) - OneTick);
        (await manager.ExistsAsync("key")).Should().BeTrue();

        _time.Advance(OneTick);
        (await manager.ExistsAsync("key")).Should().BeFalse();
    }

    [Fact]
    public async Task Sliding_UnreadEntry_ExpiresExactlyOneWindowAfterTheLoad()
    {
        var manager = CreateManager();
        var policy = Policy(TimeSpan.FromMinutes(30)) with { SlidingExpiration = TimeSpan.FromMinutes(5) };
        var counter = new Counter();

        await manager.GetOrCreateFastAsync("a", "M", counter.Next, policy);
        await manager.GetOrCreateFastAsync("b", "M", counter.Next, policy);

        _time.Advance(TimeSpan.FromMinutes(5) - OneTick);
        (await manager.ExistsAsync("a")).Should().BeTrue();

        _time.Advance(OneTick);
        (await manager.ExistsAsync("b")).Should().BeFalse();
    }

    [Fact]
    public async Task Sliding_ReadJustBeforeTheDuration_DoesNotExtendTheEntryPastIt()
    {
        var manager = CreateManager();
        var duration = TimeSpan.FromMinutes(10);
        var policy = Policy(duration) with { SlidingExpiration = TimeSpan.FromMinutes(4) };
        var counter = new Counter();

        await manager.GetOrCreateFastAsync("key", "M", counter.Next, policy);
        for (var i = 0; i < 3; i++) // reads at 3, 6 and 9 minutes keep it alive
        {
            _time.Advance(TimeSpan.FromMinutes(3));
            (await manager.GetOrCreateFastAsync("key", "M", counter.Next, policy)).Should().Be(1);
        }

        _time.Advance(TimeSpan.FromMinutes(1) - OneTick); // one tick before the Duration
        (await manager.GetOrCreateFastAsync("key", "M", counter.Next, policy)).Should().Be(1);

        _time.Advance(OneTick); // exactly at the Duration, although the last read was one tick ago
        (await manager.GetOrCreateFastAsync("key", "M", counter.Next, policy)).Should().Be(2);
    }

    [Fact]
    public async Task FailSafe_ServesTheStaleValueUntilOneTickBeforeTheWindowEnds()
    {
        var manager = CreateManager();
        var policy = Policy(TimeSpan.FromMinutes(1)) with { FailSafe = TimeSpan.FromMinutes(10) };

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);

        _time.Advance(TimeSpan.FromMinutes(11) - OneTick);
        var result = await manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), policy);

        result.Should().Be("stale");
    }

    [Fact]
    public async Task FailSafe_StopsServingTheStaleValueExactlyWhenTheWindowEnds()
    {
        var manager = CreateManager();
        var policy = Policy(TimeSpan.FromMinutes(1)) with { FailSafe = TimeSpan.FromMinutes(10) };

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);

        _time.Advance(TimeSpan.FromMinutes(11));
        var act = () => manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), policy);

        await act.Should().ThrowAsync<TimeoutException>("the stale value is gone once Duration + FailSafe has passed");
    }

    [Fact]
    public async Task FailSafeTimeout_WaitsForTheLoadUntilExactlyTheTimeout()
    {
        var manager = CreateManager();
        var policy = Policy(TimeSpan.FromMinutes(1)) with { FailSafe = TimeSpan.FromHours(1), FailSafeTimeout = TimeSpan.FromMilliseconds(200) };
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("stale"), policy);
        _time.Advance(TimeSpan.FromMinutes(1));

        var call = manager.GetOrCreateFastAsync("key", "M", () => gate.Task, policy);

        _time.Advance(TimeSpan.FromMilliseconds(200) - OneTick);
        call.IsCompleted.Should().BeFalse("the timeout has not passed yet");

        _time.Advance(OneTick);
        (await call).Should().Be("stale");

        gate.SetResult("fresh");
    }

    [Fact]
    public async Task DistributedOperationTimeout_GivesUpOnAHangingCacheExactlyAtTheTimeout()
    {
        var manager = CreateManager(distributed: CreateLayer(new HangingDistributedCache(), o => o.OperationTimeout = TimeSpan.FromMilliseconds(500)));

        var call = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("loaded"), Policy(TimeSpan.FromMinutes(1)));

        _time.Advance(TimeSpan.FromMilliseconds(500) - OneTick);
        call.IsCompleted.Should().BeFalse("the distributed read is still within its timeout");

        _time.Advance(OneTick);
        (await call).Should().Be("loaded");
    }

    [Fact]
    public async Task DistributedEntry_KeepsItsOriginalExpiry_WhenAnotherInstanceReadsItLater()
    {
        var shared = new MethodCache.TestSupport.AtomicTestCache();
        var first = CreateManager(distributed: CreateLayer(shared));
        var second = CreateManager(distributed: CreateLayer(shared));
        var policy = Policy(TimeSpan.FromSeconds(10));

        await first.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v1"), policy);

        // The second instance copies the entry 6 seconds in: it must expire with the original, not 10 seconds later.
        _time.Advance(TimeSpan.FromSeconds(6));
        (await second.GetOrCreateFastAsync("key", "M", () => Task.FromResult("unused"), policy)).Should().Be("v1");

        _time.Advance(TimeSpan.FromSeconds(4) - TimeSpan.FromMilliseconds(1));
        (await second.ExistsAsync("key")).Should().BeTrue();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await second.ExistsAsync("key")).Should().BeFalse();
        (await second.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v2"), policy)).Should().Be("v2");
    }

    [Fact]
    public async Task BackgroundCleanup_RunsOnTheInjectedClock()
    {
        var manager = CreateManager(o =>
        {
            o.EnableBackgroundCleanup = true;
            o.CleanupInterval = TimeSpan.FromMinutes(1);
        });

        await manager.SetAsync("key", "value", TimeSpan.FromSeconds(30));
        manager.Engine.Count.Should().Be(1);

        // Nobody reads the entry, so only the maintenance timer can remove it.
        _time.Advance(TimeSpan.FromMinutes(2));

        manager.Engine.Count.Should().Be(0);
        manager.Dispose();
    }

    /// <summary>Never answers until the caller's token is cancelled.</summary>
    private sealed class HangingDistributedCache : MethodCache.Extensibility.IAtomicInvalidationStore
    {
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default) => Task.Delay(Timeout.Infinite, cancellationToken);
        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return null;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            Task.Delay(Timeout.Infinite, token);

        public Task RefreshAsync(string key, CancellationToken token = default) => Task.Delay(Timeout.Infinite, token);

        public Task RemoveAsync(string key, CancellationToken token = default) => Task.Delay(Timeout.Infinite, token);
    }
}
