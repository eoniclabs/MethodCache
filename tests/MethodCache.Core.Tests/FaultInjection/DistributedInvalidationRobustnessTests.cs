using FluentAssertions;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>
/// Invalidations reaching the distributed cache in hard cases: a backplane message racing a stamp read, a backlog
/// queued during an outage, a shutdown with invalidations queued, one slow write, and a clock running ahead.
/// </summary>
public class DistributedInvalidationRobustnessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RealTimeLimit = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly IDistributedCache _shared = new MethodCache.TestSupport.AtomicTestCache();
    private readonly ChaosBackplaneHub _hub;

    public DistributedInvalidationRobustnessTests()
    {
        _hub = new ChaosBackplaneHub(_time);
    }

    private (InMemoryCacheManager Node, DistributedCacheLayer Layer) CreateNode(
        IDistributedCache? cache = null, bool backplane = false, TimeProvider? time = null, ILogger? logger = null)
    {
        time ??= _time;
        var layer = new DistributedCacheLayer(
            cache ?? _shared,
            new JsonCacheSerializer(),
            new DistributedCacheOptions
            {
                OperationTimeout = Timeout,
                CircuitBreakerDuration = BreakDuration,
                ClockSkewTolerance = TimeSpan.Zero,
            },
            backplane ? _hub.Join() : null,
            time,
            logger);

        var node = new InMemoryCacheManager(
            new NullCacheMetricsProvider(),
            MsOptions.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }),
            time,
            layer);
        return (node, layer);
    }

    private ChaosDistributedCache Chaos() => new(_shared, _time);

    private static CacheRuntimePolicy Policy(params string[] tags) =>
        CacheRuntimePolicy.Empty("M") with { Duration = OneMinute, Tags = tags };

    private static Task<string> Get(ICacheManager node, string key, Func<string> value, params string[] tags) =>
        node.GetOrCreateFastAsync(key, "M", () => Task.FromResult(value()), Policy(tags));

    private async Task AdvanceUntilDone(Task task, TimeSpan step)
    {
        var deadline = DateTime.UtcNow + RealTimeLimit;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The task did not complete while time advanced.");
            _time.Advance(step);
            await Task.WhenAny(task, Task.Delay(10));
        }
        await task;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + RealTimeLimit;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the work the fake clock released should have run");
            await Task.Delay(1);
        }
    }

    // ---------- A backplane tag invalidation arriving while a stamp read is in flight ----------

    [Fact]
    public async Task TagInvalidation_ArrivingDuringStampRead_IsNotForgotten()
    {
        var chaos = Chaos();
        var (a, _) = CreateNode(backplane: true);
        var (b, _) = CreateNode(cache: chaos, backplane: true);

        // A loads two entries carrying tag T into L2
        (await Get(a, "k1", () => "old1", "T")).Should().Be("old1");
        (await Get(a, "k2", () => "old2", "T")).Should().Be("old2");
        _time.Advance(OneMillisecond);

        // B's first read of T's stamp reaches the cache now but answers later
        chaos.Inject(ChaosOperations.Get, ChaosFault.SlowResponse(TimeSpan.FromMilliseconds(100)) with
        {
            Keys = key => key.EndsWith(":t:T", StringComparison.Ordinal),
            Times = 1,
        });
        var first = Get(b, "k1", () => "new1", "T");
        await WaitUntil(() => chaos.Blocked == 1);

        // A invalidates T while B's stamp read is in flight; the hub delivers the message to B at once
        await a.InvalidateByTagsAsync("T");

        _time.Advance(TimeSpan.FromMilliseconds(100));
        await first.WaitAsync(RealTimeLimit); // raced with the invalidation, not asserted

        // A new load on B, started after the invalidation, must not take k2's pre-invalidation entry
        (await Get(b, "k2", () => "new2", "T")).Should().Be("new2", "T was invalidated after k2 was written");
    }

    [Fact]
    public async Task TagInvalidation_ArrivingDuringStampRead_DoesNotLeaveTheStaleValueInMemory()
    {
        var chaos = Chaos();
        var (a, _) = CreateNode(backplane: true);
        var (b, _) = CreateNode(cache: chaos, backplane: true);

        (await Get(a, "k1", () => "old1", "T")).Should().Be("old1");
        (await Get(a, "k2", () => "old2", "T")).Should().Be("old2");
        _time.Advance(OneMillisecond);

        chaos.Inject(ChaosOperations.Get, ChaosFault.SlowResponse(TimeSpan.FromMilliseconds(100)) with
        {
            Keys = key => key.EndsWith(":t:T", StringComparison.Ordinal),
            Times = 1,
        });
        var first = Get(b, "k1", () => "new1", "T");
        await WaitUntil(() => chaos.Blocked == 1);
        await a.InvalidateByTagsAsync("T");
        _time.Advance(TimeSpan.FromMilliseconds(100));
        await first.WaitAsync(RealTimeLimit);
        await Get(b, "k2", () => "new2", "T");

        // Long after the stamp cache (TagCheckInterval, 1 s) would have been re-read, still inside k2's minute
        _time.Advance(TimeSpan.FromSeconds(5));
        (await Get(b, "k2", () => "new2", "T")).Should().Be("new2", "the invalidated entry must not have been promoted into memory");
    }

    // ---------- A large backlog queued during an outage ----------

    /// <summary>
    /// A store that does one stamp write or removal per millisecond, in arrival order, like a database with a
    /// bounded number of connections: an operation sent while N others wait completes N ms later.
    /// </summary>
    private sealed class ThrottledStore(IAtomicInvalidationStore inner, TimeProvider time, TimeSpan perOperation) : IAtomicInvalidationStore
    {
        private readonly object _lock = new();
        private DateTimeOffset _busyUntil;

        private Task Slot()
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = time.GetUtcNow();
                _busyUntil = (_busyUntil > now ? _busyUntil : now) + perOperation;
                wait = _busyUntil - now;
            }

            // The server works through what it was sent, whether the client still waits or not
            return Task.Delay(wait, time);
        }

        public byte[]? Get(string key) => inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => inner.SetAsync(key, value, options, token);
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);

        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            await Slot().ConfigureAwait(false);
            await inner.RemoveAsync(key, CancellationToken.None).ConfigureAwait(false);
        }

        public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
        {
            await Slot().ConfigureAwait(false);
            await inner.WriteInvalidationStampAsync(key, timestamp, retention, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task QueuedBacklog_LargerThanTheStoreDoesInOneTimeout_DrainsOnceTheStoreIsBack()
    {
        var throttled = new ThrottledStore((IAtomicInvalidationStore)_shared, _time, OneMillisecond);
        var chaos = new ChaosDistributedCache(throttled, _time).Inject(ChaosOperations.Set | ChaosOperations.Remove, ChaosFault.Throw());
        var (_, layer) = CreateNode(chaos);

        // 2,000 per-entity invalidations made during an outage
        await layer.RemoveAsync(Enumerable.Range(0, 2000).Select(i => "k" + i).ToArray());
        layer.IsAvailable.Should().BeFalse();
        chaos.Heal();

        // Two minutes of retries: the store could do 2,000 operations in 2 s, a hundred times over
        for (var step = 0; step < 2400 && !layer.IsAvailable; step++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }

        layer.IsAvailable.Should().BeTrue("a backlog the store can take at its own pace must drain");
    }

    [Fact]
    public async Task QueueLongerThanItsLimit_IsReplacedByOneClear()
    {
        var (writer, _) = CreateNode();
        (await Get(writer, "unrelated", () => "old")).Should().Be("old");
        _time.Advance(OneMillisecond);

        var chaos = Chaos().Inject(ChaosOperations.Set | ChaosOperations.Remove, ChaosFault.Throw());
        var (_, layer) = CreateNode(chaos);
        await layer.RemoveAsync(Enumerable.Range(0, 10_001).Select(i => "k" + i).ToArray());
        chaos.Heal();

        _time.Advance(BreakDuration);
        await WaitUntil(() => layer.IsAvailable);

        // One clear stood for all of them, so it also invalidated what they didn't name
        var (fresh, _) = CreateNode();
        (await Get(fresh, "unrelated", () => "new")).Should().Be("new");
    }

    [Fact]
    public async Task InvalidationTheStoreKeepsRefusing_IsReplacedByAClear_SoTheOthersAndReadsGoOn()
    {
        var (writer, _) = CreateNode();
        (await Get(writer, "key", () => "old")).Should().Be("old");
        (await Get(writer, "unrelated", () => "unrelated")).Should().Be("unrelated");
        _time.Advance(OneMillisecond);

        // The store rejects the removal stamp of one key, every time, and answers everything else
        var chaos = Chaos().Inject(ChaosOperations.Set, ChaosFault.Throw() with { Keys = key => key.EndsWith(":r:poison", StringComparison.Ordinal) });
        var (node, layer) = CreateNode(chaos);
        await node.InvalidateByKeysAsync("key", "poison");

        var (fresh, _) = CreateNode();
        (await Get(fresh, "key", () => "new")).Should().Be("new", "the other removal was stored at once");
        layer.IsAvailable.Should().BeFalse();

        for (var retry = 0; retry < 6 && !layer.IsAvailable; retry++)
        {
            _time.Advance(BreakDuration);
            await Task.Delay(10);
        }

        layer.IsAvailable.Should().BeTrue("the refused removal became a clear, which the store took");
        (await Get(CreateNode().Node, "unrelated", () => "cleared")).Should().Be("cleared");
    }

    // ---------- Shutting down with invalidations queued ----------

    [Fact]
    public async Task QueuedInvalidation_IsNotLostWhenTheNodeShutsDown()
    {
        var (writer, _) = CreateNode();
        (await Get(writer, "key", () => "old")).Should().Be("old");
        _time.Advance(OneMillisecond);

        var chaos = Chaos().Inject(ChaosOperations.Set | ChaosOperations.Remove, ChaosFault.Throw());
        var (node, layer) = CreateNode(chaos);
        await node.InvalidateByKeysAsync("key"); // queued: the store refused it
        layer.IsAvailable.Should().BeFalse();
        chaos.Heal();                            // the store is back just as the node stops
        await layer.DisposeAsync();

        var (fresh, _) = CreateNode();
        (await Get(fresh, "key", () => "new")).Should().Be("new", "the removal was accepted by the app before shutdown");
    }

    [Fact]
    public async Task ShutdownWhileTheStoreIsDown_GivesUpOnTheQueueWithinTheTimeout()
    {
        var chaos = Chaos().Inject(ChaosOperations.Set | ChaosOperations.Remove, ChaosFault.HangUntilCancelled());
        var (node, layer) = CreateNode(chaos);
        var invalidation = node.InvalidateByKeysAsync("key");
        _time.Advance(Timeout);
        await invalidation.WaitAsync(RealTimeLimit);
        layer.IsAvailable.Should().BeFalse();

        var blocked = chaos.Blocked;
        var shutdown = layer.DisposeAsync().AsTask();
        await WaitUntil(() => chaos.Blocked > blocked);
        _time.Advance(Timeout);
        await shutdown.WaitAsync(RealTimeLimit);
    }

    /// <summary>A node whose invalidation of "key" was queued, and whose retry of it now hangs in the store.</summary>
    private async Task<(ChaosDistributedCache Chaos, DistributedCacheLayer Layer)> RetryInFlight(RecordingLogger logger)
    {
        var (writer, _) = CreateNode();
        (await Get(writer, "key", () => "old")).Should().Be("old");
        _time.Advance(OneMillisecond);

        var chaos = Chaos().Inject(ChaosOperations.Set | ChaosOperations.Remove, ChaosFault.HangUntilCancelled());
        var (node, layer) = CreateNode(chaos, logger: logger);
        var invalidation = node.InvalidateByKeysAsync("key");
        _time.Advance(Timeout);
        await invalidation.WaitAsync(RealTimeLimit);
        layer.IsAvailable.Should().BeFalse();

        var blocked = chaos.Blocked;
        _time.Advance(BreakDuration); // the retry timer fires
        await WaitUntil(() => chaos.Blocked > blocked);
        return (chaos, layer);
    }

    [Fact]
    public async Task ShutdownDuringARetry_WaitsForIt_AndStoresWhatItGaveBack()
    {
        var (chaos, layer) = await RetryInFlight(new RecordingLogger());
        chaos.Heal();

        var shutdown = layer.DisposeAsync().AsTask();
        _time.Advance(Timeout); // the retry gives up, and shutdown stores the invalidation itself
        await shutdown.WaitAsync(RealTimeLimit);

        var (fresh, _) = CreateNode();
        (await Get(fresh, "key", () => "new")).Should().Be("new", "the removal the retry gave back was stored at shutdown");
    }

    [Fact]
    public async Task ShutdownDuringARetry_WhileTheStoreStaysDown_CountsWhatTheRetryGaveBackAsLost()
    {
        var logger = new RecordingLogger();
        var (chaos, layer) = await RetryInFlight(logger);

        var shutdown = layer.DisposeAsync().AsTask();
        var started = chaos.FaultsStarted;
        // The retry gives up, and so does shutdown's own try. Keep advancing rather than once per try: shutdown's
        // try may start its timeout just after a single advance, which would then wait for real time.
        await AdvanceUntilDone(shutdown, Timeout);
        chaos.FaultsStarted.Should().BeGreaterThan(started, "shutdown tried the store itself after the retry gave up");

        logger.Messages.Should().ContainSingle(m => m.Level == LogLevel.Warning && m.Message.Contains("shutting down with 1 invalidations", StringComparison.Ordinal));
    }

    // ---------- One write that times out ----------

    [Fact]
    public async Task OneSlowWrite_DoesNotTurnOffL2ReadsOfOtherKeys()
    {
        var (writer, _) = CreateNode();
        (await Get(writer, "other", () => "shared")).Should().Be("shared");

        var chaos = Chaos().Inject(ChaosOperations.Set, ChaosFault.Slow(TimeSpan.FromHours(1)) with
        {
            Keys = key => key.EndsWith(":e:big", StringComparison.Ordinal),
        });
        var (node, _) = CreateNode(chaos);

        var big = Get(node, "big", () => "large value");
        _time.Advance(Timeout);
        (await big.WaitAsync(RealTimeLimit)).Should().Be("large value");

        (await Get(node, "other", () => "loaded again")).Should().Be("shared", "the store answers reads fine; only one value was slow to write");

        // The write may still land after a removal it never saw, so the key is removed once it had time to
        _time.Advance(BreakDuration);
        await WaitUntil(() => _shared.Get("methodcache:r:big") != null);
    }

    [Fact]
    public async Task SlowWrite_PendingRemoval_IsDoneAtShutdownInsteadOfAfterIt()
    {
        var chaos = Chaos().Inject(ChaosOperations.Set, ChaosFault.Slow(TimeSpan.FromHours(1)) with
        {
            Keys = key => key.EndsWith(":e:big", StringComparison.Ordinal),
        });
        var (node, layer) = CreateNode(chaos);
        var big = Get(node, "big", () => "large value");
        _time.Advance(Timeout);
        await big.WaitAsync(RealTimeLimit);

        await layer.DisposeAsync().AsTask().WaitAsync(RealTimeLimit);
        _shared.Get("methodcache:r:big").Should().NotBeNull("shutdown removes the key rather than leave a delay running");

        // Nothing waits on the removal's delay any more, so nothing reaches the store after shutdown
        var calls = chaos.Calls;
        _time.Advance(BreakDuration);
        await Task.Delay(50);
        chaos.Calls.Should().Be(calls);
    }

    [Fact]
    public async Task SlowWrites_OfOneKey_ShareOneRemoval()
    {
        var chaos = Chaos().Inject(ChaosOperations.Set, ChaosFault.Slow(TimeSpan.FromHours(1)) with
        {
            Keys = key => key.EndsWith(":e:big", StringComparison.Ordinal),
        });
        var (_, layer) = CreateNode(chaos);
        for (var i = 0; i < 3; i++)
        {
            var write = layer.WriteAsync("big", "large value", null, layer.Now, OneMinute);
            _time.Advance(Timeout);
            await write.WaitAsync(RealTimeLimit);
        }

        // What one removal costs the store
        var before = chaos.Calls;
        await layer.RemoveAsync(new[] { "other" });
        var oneRemoval = chaos.Calls - before;

        var calls = chaos.Calls;
        _time.Advance(BreakDuration);
        await WaitUntil(() => _shared.Get("methodcache:r:big") != null);
        _time.Advance(BreakDuration);
        await Task.Delay(50);
        chaos.Calls.Should().Be(calls + oneRemoval, "the later writes pushed the one removal back instead of adding their own");
    }

    // ---------- A node whose clock runs ahead ----------

    [Fact]
    public async Task ClearFromANodeWithAClockAhead_DoesNotMakeLaterEntriesMisses()
    {
        var ahead = new FakeTimeProvider(_time.GetUtcNow() + TimeSpan.FromMinutes(10));
        var (skewed, _) = CreateNode(time: ahead);
        var (b, _) = CreateNode();
        var (c, _) = CreateNode();
        (await Get(b, "before", () => "written before the clear")).Should().Be("written before the clear");

        await skewed.ClearAsync();
        _time.Advance(OneMillisecond);

        // The clear still applies to what was written before it, once C has seen its stamp
        (await Get(c, "before", () => "loaded again")).Should().Be("loaded again", "the entry was written before the clear");
        _time.Advance(TimeSpan.FromSeconds(1));

        (await Get(b, "k", () => "written after the clear")).Should().Be("written after the clear");
        (await Get(c, "k", () => "loaded again")).Should().Be("written after the clear", "the entry was written after the clear happened");
    }
}
