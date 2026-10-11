using FluentAssertions;
using MethodCache.Core.Tests.Core;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>
/// Two application instances, each its own service provider set up the way an application does it
/// (<c>AddMethodCache</c>, <c>AddMethodCacheDistributedCache</c>, an <see cref="IBackplane"/>), share one distributed
/// cache and one backplane that loses, delays, duplicates and reorders messages.
/// </summary>
/// <remarks>
/// What docs/features/distributed-cache.md promises, and these tests pin: invalidating a key or tag removes it from the
/// distributed cache, and the backplane tells every other instance to drop its in-memory copy at once. Without a
/// message (lost, or the instance disconnected) the other instance keeps its in-memory copy until it expires, as
/// without a backplane. Messages carry no values, so a late, repeated or reordered message can only drop a copy.
/// </remarks>
[Collection(nameof(CpuIntensiveCollection))]
public sealed class TwoNodeFaultInjectionTests : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OneMillisecond = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan RealTimeLimit = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly IDistributedCache _shared = new MethodCache.TestSupport.AtomicTestCache();
    private readonly ChaosBackplaneHub _hub;
    private readonly List<ServiceProvider> _providers = new();

    public TwoNodeFaultInjectionTests()
    {
        _hub = new ChaosBackplaneHub(_time);
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    private InMemoryCacheManager CreateNode(IBackplane? backplane, IDistributedCache? cache = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        if (backplane != null)
        {
            services.AddSingleton(backplane);
        }

        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => cache ?? _shared, options => options.ClockSkewTolerance = TimeSpan.Zero);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        provider.GetRequiredService<ICacheManager>().Should().BeSameAs(provider.GetRequiredService<InMemoryCacheManager>());
        return provider.GetRequiredService<InMemoryCacheManager>();
    }

    private (InMemoryCacheManager A, InMemoryCacheManager B, ChaosBackplaneHub.Member BackplaneA, ChaosBackplaneHub.Member BackplaneB) CreateNodes()
    {
        var backplaneA = _hub.Join();
        var backplaneB = _hub.Join();
        return (CreateNode(backplaneA), CreateNode(backplaneB), backplaneA, backplaneB);
    }

    private static async Task<string> Get(ICacheManager node, string key, string value, params string[] tags) =>
        await node.GetOrCreateAsync(key, (_, _) => ValueTask.FromResult(value), options => options
            .WithDuration(Duration)
            .WithTags(tags));

    /// <summary>Invalidates a key or a tag, whichever <paramref name="byTag"/> says.</summary>
    private static Task Invalidate(ICacheManager node, bool byTag) =>
        byTag ? node.InvalidateByTagsAsync("users") : node.InvalidateByKeysAsync("key");

    /// <summary>Both nodes hold "v1" in memory, and the clock has moved on so an invalidation is later than the entry.</summary>
    private async Task Warm(ICacheManager a, ICacheManager b)
    {
        (await Get(a, "key", "v1", "users")).Should().Be("v1");
        (await Get(b, "key", "unused", "users")).Should().Be("v1");
        _time.Advance(OneMillisecond);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalidation_OnOneNode_IsObservedOnTheOther(bool byTag)
    {
        var (a, b, _, _) = CreateNodes();
        await Warm(a, b);

        await Invalidate(a, byTag);
        _time.Advance(OneMillisecond);

        (await Get(b, "key", "v2", "users")).Should().Be("v2");
        (await Get(a, "key", "unused", "users")).Should().Be("v2");
    }

    [Fact]
    public async Task Set_OnOneNode_IsObservedOnTheOther()
    {
        var (a, b, _, _) = CreateNodes();
        await Warm(a, b);

        await a.SetAsync("key", "v2", Duration);

        (await Get(b, "key", "unused", "users")).Should().Be("v2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DroppedMessage_TheOtherNodeKeepsItsCopyUntilItExpires(bool byTag)
    {
        var (a, b, _, _) = CreateNodes();
        await Warm(a, b);
        _hub.Mode = BackplaneChaos.Drop;

        await Invalidate(a, byTag);
        _time.Advance(OneMillisecond);
        _hub.Dropped.Should().BePositive();

        // The distributed cache is right, so an instance without a copy in memory reads the change at once
        (await Get(CreateNode(_hub.Join()), "key", "v2", "users")).Should().Be("v2");

        // The instance that missed the message serves its copy until it expires, and not longer
        (await Get(b, "key", "unused", "users")).Should().Be("v1");
        _time.Advance(Duration - TimeSpan.FromMilliseconds(3));
        (await Get(b, "key", "unused", "users")).Should().Be("v1");
        _time.Advance(TimeSpan.FromMilliseconds(2));
        (await Get(b, "key", "v2", "users")).Should().Be("v2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedMessage_TheOtherNodeKeepsItsCopyUntilTheMessageArrives(bool byTag)
    {
        var (a, b, _, _) = CreateNodes();
        await Warm(a, b);
        _hub.Mode = BackplaneChaos.Delay;
        _hub.Delay = TimeSpan.FromSeconds(2);

        await Invalidate(a, byTag);
        _time.Advance(OneMillisecond);

        (await Get(b, "key", "unused", "users")).Should().Be("v1");
        _time.Advance(_hub.Delay);
        (await Get(b, "key", "v2", "users")).Should().Be("v2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicatedMessages_OnlyDropCopies_SoTheyNeverServeAWrongValue(bool byTag)
    {
        var (a, b, _, _) = CreateNodes();
        await Warm(a, b);
        _hub.Mode = BackplaneChaos.Duplicate;

        await Invalidate(a, byTag);
        _time.Advance(OneMillisecond);
        (await Get(b, "key", "v2", "users")).Should().Be("v2");
        (await Get(a, "key", "unused", "users")).Should().Be("v2");

        // Every message arrives once more, long after: the nodes drop their newer copies and read them back
        _time.Advance(TimeSpan.FromSeconds(5));
        foreach (var message in _hub.Log)
        {
            await _hub.Redeliver(message);
        }

        (await Get(b, "key", "v3", "users")).Should().Be("v2");
        (await Get(a, "key", "v3", "users")).Should().Be("v2");
    }

    [Fact]
    public async Task ReorderedMessages_EndWithTheLatestValue()
    {
        var (a, b, _, _) = CreateNodes();
        await Warm(a, b);
        _hub.Mode = BackplaneChaos.Hold;

        await a.SetAsync("key", "v2", Duration);
        _time.Advance(OneMillisecond);
        await a.InvalidateByTagsAsync("users");
        _time.Advance(OneMillisecond);
        await a.SetAsync("key", "v3", Duration);
        _hub.HeldCount.Should().Be(3);

        // Until any message arrives, B serves its copy
        (await Get(b, "key", "unused", "users")).Should().Be("v1");

        await _hub.DeliverHeld(reverse: true);

        (await Get(b, "key", "unused", "users")).Should().Be("v3");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectedNode_MissesInvalidations_UntilItsCopyExpires_AndItsOwnDoNotThrow(bool byTag)
    {
        var (a, b, _, backplaneB) = CreateNodes();
        await Warm(a, b);
        backplaneB.Disconnected = true;

        await Invalidate(a, byTag);
        _time.Advance(OneMillisecond);
        (await Get(b, "key", "unused", "users")).Should().Be("v1", "B heard nothing");
        (await Get(a, "key", "v2", "users")).Should().Be("v2");

        // B's own invalidation can't be published, but is still stored, and doesn't fail the call
        await b.InvalidateByKeysAsync("key");
        (await Get(a, "key", "unused", "users")).Should().Be("v2", "A heard nothing either");

        // Reconnected, later messages arrive again; the copies that missed theirs expire
        backplaneB.Disconnected = false;
        _time.Advance(Duration);
        (await Get(a, "key", "v3", "users")).Should().Be("v3");
        (await Get(b, "key", "unused", "users")).Should().Be("v3");
        await a.InvalidateByKeysAsync("key");
        (await Get(b, "key", "v4", "users")).Should().Be("v4");
    }

    [Fact]
    public async Task InvalidationArrivingWhileTheOtherNodeReadsTheDistributedCache_IsNotLost()
    {
        var backplaneA = _hub.Join();
        var backplaneB = _hub.Join();
        var a = CreateNode(backplaneA);
        var slowB = new ChaosDistributedCache(_shared, _time);
        var b = CreateNode(backplaneB, slowB);
        await Get(a, "key", "v1");
        _time.Advance(OneMillisecond);

        // B's read reaches the cache now, while it still holds v1, and its answer arrives later
        slowB.Inject(ChaosOperations.Get, ChaosFault.SlowResponse(TimeSpan.FromMilliseconds(200)) with
        {
            Keys = static key => key.EndsWith(":e:key", StringComparison.Ordinal),
            Times = 1,
        });
        var read = Get(b, "key", "unused");
        await a.SetAsync("key", "v2", Duration);
        _time.Advance(TimeSpan.FromMilliseconds(200));

        // The read returns what it read, but B doesn't keep it
        (await read.WaitAsync(RealTimeLimit)).Should().Be("v1");
        (await Get(b, "key", "unused")).Should().Be("v2");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task RandomMessageFaults_NodesAgreeOnceMessagesArrive_OrCopiesExpire(int seed, bool drop)
    {
        var random = new Random(seed);
        var source = new Dictionary<string, int>();
        var keys = new[] { "k0", "k1", "k2", "k3" };
        string TagOf(string key) => key is "k0" or "k1" ? "even" : "odd";
        string Latest(string key) => $"{key}:{source.GetValueOrDefault(key)}";
        Task<string> Read(ICacheManager node, string key) => Get(node, key, Latest(key), TagOf(key));

        var nodes = new[] { CreateNode(_hub.Join()), CreateNode(_hub.Join()) };
        var modes = drop
            ? new[] { BackplaneChaos.Deliver, BackplaneChaos.Drop, BackplaneChaos.Delay, BackplaneChaos.Duplicate, BackplaneChaos.Hold }
            : new[] { BackplaneChaos.Deliver, BackplaneChaos.Delay, BackplaneChaos.Duplicate, BackplaneChaos.Hold };
        _hub.Delay = TimeSpan.FromMilliseconds(300);

        for (var step = 0; step < 300; step++)
        {
            _hub.Mode = modes[random.Next(modes.Length)];
            var node = nodes[random.Next(nodes.Length)];
            var key = keys[random.Next(keys.Length)];

            switch (random.Next(5))
            {
                case 0:
                    source[key] = source.GetValueOrDefault(key) + 1;
                    await node.InvalidateByKeysAsync(key);
                    (await Read(node, key)).Should().Be(Latest(key), "the node that invalidated never serves the old value");
                    break;

                case 1:
                    foreach (var tagged in keys.Where(k => TagOf(k) == TagOf(key)))
                    {
                        source[tagged] = source.GetValueOrDefault(tagged) + 1;
                    }

                    await node.InvalidateByTagsAsync(TagOf(key));
                    (await Read(node, key)).Should().Be(Latest(key), "the node that invalidated never serves the old value");
                    break;

                case 2:
                    if (random.Next(4) == 0)
                    {
                        await _hub.DeliverHeld(reverse: random.Next(2) == 0);
                    }

                    break;

                default:
                    var value = await Read(node, key);
                    value.Should().StartWith(key + ":");
                    int.Parse(value[(key.Length + 1)..]).Should().BeLessThanOrEqualTo(source.GetValueOrDefault(key), "no value from the future");
                    break;
            }

            _time.Advance(TimeSpan.FromMilliseconds(random.Next(0, 200)));
        }

        // Let every message arrive; lost ones are made up for by the copies expiring
        _hub.Mode = BackplaneChaos.Deliver;
        await _hub.DeliverHeld();
        _time.Advance(drop ? Duration : _hub.Delay);

        foreach (var node in nodes)
        {
            foreach (var key in keys)
            {
                (await Read(node, key)).Should().Be(Latest(key));
            }
        }
    }
}
