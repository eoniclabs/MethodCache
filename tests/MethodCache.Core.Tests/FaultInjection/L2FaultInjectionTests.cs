using FluentAssertions;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>
/// What a cached call guarantees when the distributed cache (L2) hangs, is slow, fails now and then or returns damaged
/// data. Time is a <see cref="FakeTimeProvider"/>, so "completes within X" means: once the clock has moved X, the call
/// is done, and not before.
/// </summary>
public class L2FaultInjectionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    // Only bounds the real time a test waits for work that the fake clock already released
    private static readonly TimeSpan RealTimeLimit = TimeSpan.FromSeconds(10);

    private const string EntryKey = "methodcache:e6:s1:key";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly IDistributedCache _shared = new MethodCache.TestSupport.AtomicTestCache();
    private readonly ChaosBackplaneHub _hub;

    public L2FaultInjectionTests()
    {
        _hub = new ChaosBackplaneHub(_time);
    }

    private (InMemoryCacheManager Node, DistributedCacheLayer Layer) CreateNode(IDistributedCache? cache = null, bool backplane = false)
    {
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
            _time);

        var node = new InMemoryCacheManager(
            new NullCacheMetricsProvider(),
            MsOptions.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }),
            _time,
            layer);
        return (node, layer);
    }

    private ChaosDistributedCache Chaos() => new(_shared, _time);

    private static CacheRuntimePolicy Policy(params string[] tags) =>
        CacheRuntimePolicy.Empty("M") with { Duration = OneMinute, Tags = tags };

    private static Task<string> Get(ICacheManager node, string key, Func<string> value, params string[] tags) =>
        node.GetOrCreateFastAsync(key, "M", () => Task.FromResult(value()), Policy(tags));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + RealTimeLimit;
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the work the fake clock released should have run");
            await Task.Delay(1);
        }
    }

    private static ChaosFault Fault(ChaosFaultKind kind) => kind switch
    {
        ChaosFaultKind.Throw => ChaosFault.Throw(),
        ChaosFaultKind.HangUntilCancelled => ChaosFault.HangUntilCancelled(),
        ChaosFaultKind.HangIgnoringToken => ChaosFault.HangIgnoringToken(),
        ChaosFaultKind.Delay => ChaosFault.Slow(TimeSpan.FromHours(1)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // ---------- a miss is bounded by the operation timeout ----------

    public static TheoryData<ChaosOperations, ChaosFaultKind> Stalls => new()
    {
        { ChaosOperations.Get, ChaosFaultKind.HangUntilCancelled },
        { ChaosOperations.Get, ChaosFaultKind.HangIgnoringToken },
        { ChaosOperations.Get, ChaosFaultKind.Delay },
        { ChaosOperations.Set, ChaosFaultKind.HangUntilCancelled },
        { ChaosOperations.Set, ChaosFaultKind.HangIgnoringToken },
        { ChaosOperations.Set, ChaosFaultKind.Delay },
        { ChaosOperations.All, ChaosFaultKind.HangIgnoringToken },
    };

    [Theory]
    [MemberData(nameof(Stalls))]
    public async Task Miss_WhenL2Stalls_CompletesAfterOneOperationTimeout(ChaosOperations operations, ChaosFaultKind kind)
    {
        var chaos = Chaos().Inject(operations, Fault(kind));
        var (node, _) = CreateNode(chaos);

        var miss = Get(node, "key", () => "loaded");

        _time.Advance(Timeout - OneMillisecond);
        miss.IsCompleted.Should().BeFalse("the distributed cache is given its full timeout");
        _time.Advance(OneMillisecond);
        (await miss.WaitAsync(RealTimeLimit)).Should().Be("loaded");
        chaos.FaultsStarted.Should().Be(1, "the first stall opens the breaker, so the miss meets no second one");
    }

    [Fact]
    public async Task Miss_WhenL2ReadsHang_TakesTheTimeoutPlusTheFactoryTime()
    {
        var factoryTime = TimeSpan.FromMilliseconds(300);
        var chaos = Chaos().Inject(ChaosOperations.Get, ChaosFault.HangUntilCancelled());
        var (node, _) = CreateNode(chaos);
        var factoryStarted = new TaskCompletionSource();

        var miss = node.GetOrCreateFastAsync("key", "M", async () =>
        {
            var work = Task.Delay(factoryTime, _time);
            factoryStarted.SetResult();
            await work;
            return "loaded";
        }, Policy());

        _time.Advance(Timeout);
        await factoryStarted.Task.WaitAsync(RealTimeLimit);
        _time.Advance(factoryTime - OneMillisecond);
        miss.IsCompleted.Should().BeFalse();
        _time.Advance(OneMillisecond);

        (await miss.WaitAsync(RealTimeLimit)).Should().Be("loaded");
    }

    [Theory]
    [InlineData(ChaosOperations.Get)]
    [InlineData(ChaosOperations.Set)]
    [InlineData(ChaosOperations.All)]
    public async Task Miss_WhenL2Throws_CompletesWithoutWaiting(ChaosOperations operations)
    {
        var chaos = Chaos().Inject(operations, ChaosFault.Throw());
        var (node, _) = CreateNode(chaos);

        var miss = Get(node, "key", () => "loaded");

        miss.IsCompletedSuccessfully.Should().BeTrue();
        (await miss).Should().Be("loaded");
    }

    [Fact]
    public async Task ConcurrentMisses_WhenL2Hangs_EachWaitAtMostOneTimeout()
    {
        // Misses that were already waiting when the breaker opened finish with the same timeout, and later ones
        // skip the cache: a hanging cache never adds up timeouts across callers
        var chaos = Chaos().Inject(ChaosOperations.All, ChaosFault.HangIgnoringToken());
        var (node, _) = CreateNode(chaos);

        var misses = Enumerable.Range(0, 20).Select(i => Get(node, $"key{i}", () => $"v{i}")).ToArray();
        _time.Advance(Timeout);
        var results = await Task.WhenAll(misses).WaitAsync(RealTimeLimit);

        results.Should().Equal(Enumerable.Range(0, 20).Select(i => $"v{i}"));
        var later = Get(node, "later", () => "later");
        later.IsCompletedSuccessfully.Should().BeTrue("the breaker is open");
    }

    [Fact]
    public async Task Hits_FromMemory_DoNotTouchAStalledL2()
    {
        var chaos = Chaos();
        var (node, _) = CreateNode(chaos);
        await Get(node, "key", () => "v1");

        chaos.Inject(ChaosOperations.All, ChaosFault.HangIgnoringToken());
        var calls = chaos.Calls;
        var hit = Get(node, "key", () => "unused");

        hit.IsCompletedSuccessfully.Should().BeTrue();
        (await hit).Should().Be("v1");
        chaos.Calls.Should().Be(calls);
    }

    [Fact]
    public async Task SlowL2_EachOperationIsCapped_NotTheMiss_ThreeRoundTripsPerMiss()
    {
        // Observed and documented, not a bug: the timeout caps each call to the distributed cache. A cache that
        // answers just under it costs a miss one delay per round trip: the read, the write and the removal check
        // after the write (and a backplane publish, when there is one).
        var delay = TimeSpan.FromMilliseconds(400);
        var chaos = Chaos().Inject(ChaosOperations.All, ChaosFault.Slow(delay));
        var (node, _) = CreateNode(chaos);

        var miss = Get(node, "key", () => "loaded");
        for (var roundTrip = 1; roundTrip <= 3; roundTrip++)
        {
            try { await WaitUntil(() => chaos.FaultsStarted == roundTrip && chaos.Blocked == 1); }
            catch (Exception error)
            { throw new InvalidOperationException($"Waiting for round trip {roundTrip}: started={chaos.FaultsStarted}, blocked={chaos.Blocked}, load={miss.Status}.", error); }
            miss.IsCompleted.Should().BeFalse();
            _time.Advance(delay);
        }

        (await miss.WaitAsync(RealTimeLimit)).Should().Be("loaded");
        chaos.FaultsStarted.Should().Be(3);
    }

    // ---------- damaged entries are misses ----------

    public static TheoryData<string, ChaosFault> Damages => new()
    {
        { "half", ChaosFault.Truncated() },
        { "empty", ChaosFault.Truncated(0) },
        { "header only", ChaosFault.Truncated(30) },
        { "inverted", ChaosFault.Corrupted() },
        { "random", ChaosFault.Corrupted(bytes => { new Random(42).NextBytes(bytes); return bytes; }) },
        { "last byte cut", ChaosFault.Corrupted(bytes => bytes[..^1]) },
    };

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task DamagedEntry_IsAMiss_TheFactoryRuns_AndTheEntryIsReplaced(string damage, ChaosFault fault)
    {
        _ = damage;
        var chaos = Chaos().Inject(ChaosOperations.Set, fault with { Keys = static key => key == EntryKey });
        var (writer, _) = CreateNode(chaos);
        var (reader, _) = CreateNode();
        var (third, _) = CreateNode();
        await Get(writer, "key", () => "value");

        (await Get(reader, "key", () => "fresh")).Should().Be("fresh");

        // The reader's load replaced the damaged entry
        (await Get(third, "key", () => "unused")).Should().Be("fresh");
    }

    [Fact]
    public async Task DamagedRead_IsAMiss_EvenWhenTheStoredEntryIsFine()
    {
        var (writer, _) = CreateNode();
        await Get(writer, "key", () => "value");
        var chaos = Chaos().Inject(ChaosOperations.Get, ChaosFault.Truncated() with { Keys = static key => key == EntryKey });
        var (reader, _) = CreateNode(chaos);

        (await Get(reader, "key", () => "fresh")).Should().Be("fresh");
    }

    [Fact]
    public async Task EveryTruncation_OfAnEntry_IsAMiss()
    {
        var (writer, _) = CreateNode();
        await Get(writer, "key", () => "value", "users");
        var stored = (await _shared.GetAsync(EntryKey))!;

        for (var length = 0; length < stored.Length; length++)
        {
            await _shared.SetAsync(EntryKey, stored[..length], new DistributedCacheEntryOptions());
            var (reader, _) = CreateNode();

            (await Get(reader, "key", () => "fresh", "users")).Should().Be("fresh", "{0} of {1} bytes is not an entry", length, stored.Length);
        }
    }

    [Fact]
    public async Task EveryOverwrittenByte_InAnEntry_IsAMiss_OrStillReadsTheValue_AndNeverThrows()
    {
        var (writer, _) = CreateNode();
        await Get(writer, "key", () => "value", "users");
        var stored = (await _shared.GetAsync(EntryKey))!;

        for (var position = 0; position < stored.Length; position++)
        {
            var damaged = stored.ToArray();
            damaged[position] = 0xFF;
            await _shared.SetAsync(EntryKey, damaged, new DistributedCacheEntryOptions());
            var (reader, _) = CreateNode();

            // A changed timestamp can still be a valid one, so the value may be read; anything else is a miss
            (await Get(reader, "key", () => "fresh", "users")).Should().BeOneOf(new[] { "value", "fresh" }, "byte {0} was overwritten", position);
        }
    }

    [Fact]
    public async Task BitFlipInsideTheValue_IsNotDetected()
    {
        // Observed and documented, not guaranteed: entries carry no checksum, so damage that still deserializes is
        // served. Only damage the format or the serializer can see is a miss.
        var (writer, _) = CreateNode();
        await Get(writer, "key", () => "value");
        var stored = (await _shared.GetAsync(EntryKey))!;
        var position = Array.LastIndexOf(stored, (byte)'v');
        stored[position] ^= 0x01; // 'v' -> 'w'
        await _shared.SetAsync(EntryKey, stored, new DistributedCacheEntryOptions());
        var (reader, _) = CreateNode();

        (await Get(reader, "key", () => "fresh")).Should().Be("walue");
    }

    // ---------- intermittent failures ----------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task IntermittentL2Failures_NeverServeAnInvalidatedValue_BeyondTheDocumentedRetry(int everyNth)
    {
        // The source of truth: each invalidation follows a change, so a value's version says how old it is
        var source = new Dictionary<string, int>();
        string Latest(string key) => $"{key}:{source.GetValueOrDefault(key)}";
        Func<string> Load(string key) => () => Latest(key);

        var chaos = Chaos().Inject(ChaosOperations.All, ChaosFault.Intermittent(everyNth));
        var (node, layer) = CreateNode(chaos, backplane: true);
        var (other, _) = CreateNode(backplane: true);

        for (var i = 0; i < 60; i++)
        {
            var key = $"key{i % 5}";
            (await Get(node, key, Load(key))).Should().Be(Latest(key));
            (await Get(other, key, Load(key))).Should().StartWith(key + ":");

            var clean = layer.IsAvailable;
            var faults = chaos.FaultsStarted;
            source[key] = source.GetValueOrDefault(key) + 1;
            await node.InvalidateByKeysAsync(key);
            clean &= chaos.FaultsStarted == faults;

            // On the node that invalidated, an invalidated value never comes back, whatever the cache did
            (await Get(node, key, Load(key))).Should().Be(Latest(key));

            // Elsewhere it is gone at once when the invalidation was stored; otherwise it may be served until the
            // queued invalidation is retried, every CircuitBreakerDuration
            var seen = await Get(other, key, Load(key));
            if (clean)
            {
                seen.Should().Be(Latest(key), "the invalidation was stored and published");
            }

            _time.Advance(TimeSpan.FromSeconds(3));
        }

        chaos.Heal();
        _time.Advance(BreakDuration);
        _time.Advance(BreakDuration);
        await WaitUntil(() => layer.IsAvailable);

        // Once the queued invalidations are stored, every node agrees with the source
        foreach (var key in source.Keys)
        {
            (await Get(other, key, Load(key))).Should().Be(Latest(key));
            (await Get(node, key, Load(key))).Should().Be(Latest(key));
        }
    }

    [Fact]
    public async Task L2Failure_IsNotAFactoryFailure_SoNoFailSafeValueIsServed()
    {
        var chaos = Chaos();
        var (node, _) = CreateNode(chaos);
        var policy = Policy() with { FailSafe = TimeSpan.FromHours(1) };
        await node.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v1"), policy);
        _time.Advance(OneMinute + TimeSpan.FromSeconds(1));

        chaos.Inject(ChaosOperations.All, ChaosFault.Throw());

        (await node.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v2"), policy)).Should().Be("v2");
    }

    [Fact]
    public async Task L2Failure_AndAFailingFactory_ServeTheFailSafeValue()
    {
        var chaos = Chaos();
        var (node, _) = CreateNode(chaos);
        var policy = Policy() with { FailSafe = TimeSpan.FromHours(1) };
        await node.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v1"), policy);
        _time.Advance(OneMinute + TimeSpan.FromSeconds(1));

        chaos.Inject(ChaosOperations.All, ChaosFault.Throw());

        (await node.GetOrCreateFastAsync<string>("key", "M", () => throw new InvalidOperationException("source down"), policy)).Should().Be("v1");
    }

    // ---------- writes that time out ----------

    [Fact]
    public async Task ExplicitSetWhileTheBreakerIsOpen_StillDropsTheOldValueEverywhere()
    {
        var chaos = Chaos();
        var (setter, setterLayer) = CreateNode(chaos, backplane: true);
        var (other, _) = CreateNode(backplane: true);
        await Get(setter, "key", () => "v1");
        (await Get(other, "key", () => "unused")).Should().Be("v1");

        // Another call trips the breaker, then the key is set while it is open, so the write is skipped
        chaos.Inject(ChaosOperations.All, ChaosFault.Throw());
        await Get(setter, "trip", () => "trip");
        await setter.SetAsync("key", "v2", OneMinute);
        chaos.Heal();

        // As for a write that failed, the old value is removed everywhere once the cache answers
        _time.Advance(BreakDuration);
        await WaitUntil(() => setterLayer.IsAvailable);
        (await Get(other, "key", () => "fresh")).Should().Be("fresh");
        (await Get(CreateNode().Node, "key", () => "fresh")).Should().Be("fresh");
        (await Get(setter, "key", () => "unused")).Should().Be("v2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidationTheCacheRefused_ReachesOtherNodesWithTheRetry(bool byTag)
    {
        var chaos = Chaos();
        var (node, layer) = CreateNode(chaos, backplane: true);
        var (other, _) = CreateNode(backplane: true);
        await Get(node, "key", () => "v1", "users");
        (await Get(other, "key", () => "unused", "users")).Should().Be("v1");
        _time.Advance(OneMillisecond);

        chaos.Inject(ChaosOperations.All, ChaosFault.Throw());
        await (byTag ? node.InvalidateByTagsAsync("users") : node.InvalidateByKeysAsync("key"));
        _time.Advance(OneMillisecond);

        // Documented: until the retry stores it, other nodes may serve the old value; this one never does
        (await Get(node, "key", () => "v2", "users")).Should().Be("v2");
        (await Get(other, "key", () => "unused", "users")).Should().Be("v1");

        chaos.Heal();
        _time.Advance(BreakDuration);
        await WaitUntil(() => layer.IsAvailable);
        (await Get(other, "key", () => "v2", "users")).Should().Be("v2");
    }

    [Fact]
    public async Task ExplicitSetOfAValueThatCannotBeSerialized_StillDropsTheOldValueEverywhere()
    {
        var (setter, _) = CreateNode(backplane: true);
        var (other, _) = CreateNode(backplane: true);
        Task<Box> GetBox(ICacheManager node, string value) =>
            node.GetOrCreateFastAsync("key", "M", () => Task.FromResult(new Box(value)), Policy());
        await GetBox(setter, "v1");
        (await GetBox(other, "unused")).Value.Should().Be("v1");

        await setter.SetAsync("key", new Box(Box.Unserializable), OneMinute);

        (await GetBox(other, "fresh")).Value.Should().Be("fresh");
        (await GetBox(CreateNode().Node, "fresh")).Value.Should().Be("fresh");
    }

    public sealed class Box(string value)
    {
        public const string Unserializable = "unserializable";

        public string Value => value == Unserializable ? throw new InvalidOperationException("Can't be serialized") : value;
    }

    [Fact]
    public async Task WriteThatTimesOut_AndLandsBeforeTheRetry_OverwritesANewerValue_UntilTheRetryRemovesIt()
    {
        // Observed and documented, not guaranteed: IDistributedCache has no compare-and-set, so a write that lands
        // after its timeout can overwrite a newer value. The timed-out write queued a removal of its key, so the
        // window closes with that retry (CircuitBreakerDuration); the newer value goes too, and the next read loads.
        var chaos = Chaos().Inject(ChaosOperations.Set, ChaosFault.HangIgnoringToken() with { Keys = static key => key == EntryKey, Times = 1 });
        var (late, lateLayer) = CreateNode(chaos, backplane: true);
        var (newer, _) = CreateNode(backplane: true);

        var load = Get(late, "key", () => "old");
        _time.Advance(Timeout);
        (await load.WaitAsync(RealTimeLimit)).Should().Be("old");

        _time.Advance(OneMillisecond);
        await newer.SetAsync("key", "new", OneMinute);
        (await Get(CreateNode().Node, "key", () => "unused")).Should().Be("new");

        chaos.ReleaseHeld();
        await WaitUntil(() => chaos.LandedLate == 1);
        (await Get(CreateNode().Node, "key", () => "unused")).Should().Be("old", "the late write overwrote the newer value");

        // The removal runs in the background once the delay passes; wait for it to remove the entry and tell the
        // other nodes, rather than for the breaker, which a timed-out write doesn't open
        var delivered = _hub.Delivered;
        _time.Advance(BreakDuration);
        await WaitUntil(() => _shared.Get(EntryKey) == null && _hub.Delivered > delivered);
        lateLayer.IsAvailable.Should().BeTrue();
        (await Get(CreateNode().Node, "key", () => "fresh")).Should().Be("fresh", "the retry removed the late write");
        (await Get(newer, "key", () => "fresh")).Should().Be("fresh", "the retry told the other nodes");
    }

    [Fact]
    public async Task WriteThatTimesOut_AndLandsAfterTheRetry_IsRejectedByRemovalMetadata()
    {
        // A late transport may replace the physical bytes, but its original load timestamp
        // predates the removal metadata and must never become readable again.
        var chaos = Chaos().Inject(ChaosOperations.Set, ChaosFault.HangIgnoringToken() with { Keys = static key => key == EntryKey, Times = 1 });
        var (late, lateLayer) = CreateNode(chaos, backplane: true);
        var (writer, _) = CreateNode(backplane: true);

        var load = Get(late, "key", () => "old");
        _time.Advance(Timeout);
        (await load.WaitAsync(RealTimeLimit)).Should().Be("old");
        var delivered = _hub.Delivered;
        _time.Advance(BreakDuration);
        await WaitUntil(() => _hub.Delivered > delivered); // the removal ran and told the other nodes
        lateLayer.IsAvailable.Should().BeTrue();

        _time.Advance(OneMillisecond);
        await writer.SetAsync("key", "new", OneMinute);

        chaos.ReleaseHeld();
        await WaitUntil(() => chaos.LandedLate == 1);

        (await Get(CreateNode().Node, "key", () => "unused")).Should().Be("unused", "the late write predates the removal metadata");
        _time.Advance(OneMinute);
        (await Get(CreateNode().Node, "key", () => "fresh")).Should().Be("fresh");
    }

    [Fact]
    public void ChaosCache_FaultsEveryNthMatchingCall_AndOnlyAsOftenAsAsked()
    {
        var chaos = Chaos().Inject(ChaosOperations.Get, ChaosFault.Intermittent(3) with { Times = 2 });

        var outcomes = Enumerable.Range(0, 12).Select(_ =>
        {
            try
            {
                chaos.Get("k");
                return "ok";
            }
            catch (ChaosException)
            {
                return "fault";
            }
        }).ToArray();

        outcomes.Should().Equal("ok", "ok", "fault", "ok", "ok", "fault", "ok", "ok", "ok", "ok", "ok", "ok");
    }
}
