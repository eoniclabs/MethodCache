using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class CrossFormatInvalidationTests
{
    [Fact]
    public async Task DelayedTransport_CannotMoveThePhysicalDeadlinePastMetadataRetention()
    {
        var clock = new FakeTimeProvider();
        var backend = new ExpiringAtomicCache(clock);
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeWrite = async _ => { began.TrySetResult(); await finish.Task; };
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(),
            new DistributedCacheOptions { TagRetention = TimeSpan.FromSeconds(1), ClockSkewTolerance = TimeSpan.Zero }, timeProvider: clock);
        var source = clock.GetUtcNow();
        var write = layer.WriteAsync("slow", "value", null, layer.Now, TimeSpan.FromHours(1));
        await began.Task;
        clock.Advance(TimeSpan.FromMilliseconds(700));
        finish.TrySetResult();
        await write;
        Assert.Equal(source + TimeSpan.FromSeconds(1), backend.ExpirationFor("methodcache:e6:s1:slow"));
    }

    [Theory]
    [InlineData(6, "tag")]
    [InlineData(7, "tag")]
    [InlineData(6, "method")]
    [InlineData(7, "method")]
    [InlineData(6, "clear")]
    [InlineData(7, "clear")]
    public async Task LogicalInvalidations_RetireAllFormatsAndPolicyIdentities(int removerFormat, string operation)
    {
        var clock = new FakeTimeProvider();
        var store = new ExpiringAtomicCache(clock);
        DistributedCacheLayer Node(byte format) => new(store, new JsonCacheSerializer(),
            new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero, TagCheckInterval = TimeSpan.Zero }, timeProvider: clock, entryVersion: format);
        await using var old = Node(6);
        await using var next = Node(7);
        const string tag = "\0method:fake\ud800:å";
        const string method = "N.Interface.Method\udfff";
        foreach (var node in new[] { old, next })
            foreach (var identity in new[] { "shared", "private" })
                await node.WriteAsync("key", "value", [tag], node.Now, TimeSpan.FromHours(1), identity: identity, methodId: method);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var remover = removerFormat == 6 ? old : next;
        if (operation == "tag") await remover.InvalidateTagsAsync([tag]);
        else if (operation == "method") await remover.InvalidateMethodAsync(method);
        else await remover.ClearAsync();
        clock.Advance(TimeSpan.FromMinutes(6));
        foreach (var node in new[] { old, next })
            foreach (var identity in new[] { "shared", "private" })
                Assert.False((await node.TryGetAsync<string>("key", identity: identity)).Found);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    public async Task KeyRemoval_OutlivesSixMinutes_AndRetiresEveryEntryFormatAndPolicy(int removerFormat)
    {
        var clock = new FakeTimeProvider();
        var store = new ExpiringAtomicCache(clock);
        DistributedCacheLayer Node(byte format) => new(store, new JsonCacheSerializer(),
            new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero, TagCheckInterval = TimeSpan.Zero }, timeProvider: clock, entryVersion: format);
        await using var old = Node(6);
        await using var next = Node(7);
        foreach (var identity in new[] { "shared-policy", "private-policy" })
        {
            await old.WriteAsync("key", "old", null, old.Now, TimeSpan.FromHours(1), identity: identity);
            await next.WriteAsync("key", "next", null, next.Now, TimeSpan.FromHours(1), identity: identity);
        }
        Assert.True((await old.TryGetAsync<string>("key", identity: "shared-policy")).Found);
        Assert.True((await next.TryGetAsync<string>("key", identity: "shared-policy")).Found);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await (removerFormat == 6 ? old : next).RemoveAsync(["key"]);
        clock.Advance(TimeSpan.FromMinutes(6));
        foreach (var identity in new[] { "shared-policy", "private-policy" })
        {
            Assert.False((await old.TryGetAsync<string>("key", identity: identity)).Found);
            Assert.False((await next.TryGetAsync<string>("key", identity: identity)).Found);
        }
    }
}
