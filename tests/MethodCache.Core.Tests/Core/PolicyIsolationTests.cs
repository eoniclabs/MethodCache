using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class PolicyIsolationTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);
    private readonly MethodCache.TestSupport.AtomicTestCache _store = new();

    private InMemoryCacheManager Node(bool cacheNull = true)
        => new(new NullCacheMetricsProvider(), Microsoft.Extensions.Options.Options.Create(new MethodCacheMemoryOptions
        { EnableBackgroundCleanup = false, CacheNullValues = cacheNull }), _time,
            new DistributedCacheLayer(_store, new JsonCacheSerializer(), new DistributedCacheOptions
            { ClockSkewTolerance = TimeSpan.Zero }, timeProvider: _time));

    [Fact]
    public async Task DistributedNull_DisallowedByReceiver_RunsOriginInstead()
    {
        using var first = Node();
        using var receiver = Node(cacheNull: false);
        var policy = CacheRuntimePolicy.Empty("Get") with { Duration = TimeSpan.FromSeconds(10) };
        Assert.Null(await first.GetOrCreateFastAsync<string?>("null", "Get", () => Task.FromResult<string?>(null), policy));
        Assert.Equal("origin", await receiver.GetOrCreateFastAsync("null", "Get", () => Task.FromResult("origin"), policy));
    }

    [Fact]
    public async Task DistributedPredicate_RejectsStoredValue_RunsOriginInstead()
    {
        using var first = Node();
        using var receiver = Node();
        var policy = CacheRuntimePolicy.Empty("Get") with { Duration = TimeSpan.FromSeconds(10) };
        Assert.Equal(1, await first.GetOrCreateFastAsync("predicate", "Get", () => Task.FromResult(1), policy));
        var restricted = policy with { CacheWhen = static value => value is int number && number > 1 };
        Assert.Equal(2, await receiver.GetOrCreateFastAsync("predicate", "Get", () => Task.FromResult(2), restricted));
    }

    [Fact]
    public async Task DistributedDisabled_DoesNotReadOrWriteSharedValues()
    {
        using var first = Node();
        using var receiver = Node();
        var policy = CacheRuntimePolicy.Empty("Get") with { Duration = TimeSpan.FromSeconds(10) };
        Assert.Equal(1, await first.GetOrCreateFastAsync("local", "Get", () => Task.FromResult(1), policy));
        Assert.Equal(2, await receiver.GetOrCreateFastAsync("local", "Get", () => Task.FromResult(2), policy with { Distributed = false }));
        using var third = Node();
        Assert.Equal(1, await third.GetOrCreateFastAsync("local", "Get", () => Task.FromResult(3), policy));
    }
    [Fact]
    public async Task DistributedSlidingFailSafe_CannotOutliveTheStoredDeadline()
    {
        using var writer = Node();
        using var receiver = Node();
        var policy = CacheRuntimePolicy.Empty("Get") with { Duration = TimeSpan.FromSeconds(10) };
        Assert.Equal(1, await writer.GetOrCreateFastAsync("deadline", "Get", () => Task.FromResult(1), policy));
        var sliding = policy with { Duration = TimeSpan.FromSeconds(30), SlidingExpiration = TimeSpan.FromSeconds(2), FailSafe = TimeSpan.FromSeconds(5) };
        Assert.Equal(1, await receiver.GetOrCreateFastAsync("deadline", "Get", () => Task.FromResult(2), sliding));
        for (var i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1500));
            Assert.Equal(1, await receiver.GetOrCreateFastAsync("deadline", "Get", () => Task.FromResult(2), sliding));
        }
        _time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.GetOrCreateFastAsync<int>("deadline", "Get", () => Task.FromException<int>(new InvalidOperationException()), sliding));
    }

}
