using System.Buffers.Binary;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Xunit;

namespace MethodCache.TestSupport;

// Objects keep this shared test helper's public signature independent of internal provider contracts.
public static class EpochBackendContract
{
    public static async Task RunAsync(object firstProvider, object secondProvider, string prefix)
    {
        var first = (IDistributedEpochStore)firstProvider;
        var second = (IDistributedEpochStore)secondProvider;
        var stamps = (IAtomicInvalidationStore)firstProvider;
        var key = prefix + "tag";
        var initial = (await first.EnsureEpochsAsync([key]))[0];
        Assert.NotEqual(Guid.Empty, initial.Initialization);
        Assert.Equal(initial, (await second.EnsureEpochsAsync([key]))[0]);
        var changes = await Task.WhenAll(Enumerable.Range(0, 64).Select(i =>
            (i % 2 == 0 ? first : second).AdvanceEpochAsync(key).AsTask()));
        Assert.Equal(64, changes.Distinct().Count());
        Assert.All(changes, item => Assert.Equal(initial.Initialization, item.Initialization));
        var current = (await first.EnsureEpochsAsync([key]))[0];
        Assert.Equal(64UL, current.Revision);
        await stamps.WriteInvalidationStampAsync(key, 1234, TimeSpan.FromMinutes(1));
        var raw = await stamps.GetAsync(key);
        Assert.NotNull(raw);
        Assert.Equal(1234, BinaryPrimitives.ReadInt64LittleEndian(raw));
        Assert.True(EpochMetadata.TryRead(raw, out var atomic));
        Assert.Equal(current.Initialization, atomic.Initialization);
        Assert.Equal(65UL, atomic.Revision);

        // Seeding extreme revisions is test setup; native operations must preserve all 64 bits.
        foreach (var revision in new[] { (ulong)uint.MaxValue, (1UL << 53) + 7, (1UL << 63) + 7, ulong.MaxValue })
        {
            var bytes = EpochMetadata.Ensure(null, Guid.NewGuid());
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(25), revision);
            _ = EpochMetadata.TryRead(bytes, out var seeded);
            await stamps.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1) });
            var advanced = await second.AdvanceEpochAsync(key);
            if (revision == ulong.MaxValue)
            {
                Assert.NotEqual(seeded.Initialization, advanced.Initialization);
                Assert.Equal(0UL, advanced.Revision);
                var retriedOldOperation = await first.AdvanceEpochAsync(key);
                Assert.Equal(advanced.Initialization, retriedOldOperation.Initialization);
                Assert.Equal(1UL, retriedOldOperation.Revision);
            }
            else
            {
                Assert.Equal(seeded.Initialization, advanced.Initialization);
                Assert.Equal(revision + 1, advanced.Revision);
            }
        }
        await stamps.RemoveAsync(key);
        var recreated = (await second.EnsureEpochsAsync([key]))[0];
        Assert.NotEqual(current.Initialization, recreated.Initialization);
        var vector = await first.EnsureEpochsAsync([prefix + "global", prefix + "client", prefix + "url", key]);
        Assert.Equal(4, vector.Length);
        Assert.All(vector, epoch => Assert.NotEqual(Guid.Empty, epoch.Initialization));
        Assert.Equal(recreated, vector[3]);

        // A node that only invalidates must also honor the scope contract before writing shorter-lived metadata.
        await using var writer = new MethodCache.Core.Runtime.Distributed.DistributedCacheLayer((IDistributedCache)firstProvider,
            new MethodCache.Extensibility.JsonCacheSerializer(), new DistributedCacheOptions { KeyPrefix = prefix + "core:", TagRetention = TimeSpan.FromDays(2) });
        await writer.WriteAsync("long", 1, null, writer.Now, TimeSpan.FromDays(2));
        await using var shorter = new MethodCache.Core.Runtime.Distributed.DistributedCacheLayer((IDistributedCache)secondProvider,
            new MethodCache.Extensibility.JsonCacheSerializer(), new DistributedCacheOptions { KeyPrefix = prefix + "core:", TagRetention = TimeSpan.FromDays(1) });
        await shorter.RemoveAsync(["long"]);
        Assert.True(shorter.HasIncompatibleScope);
        Assert.Equal(1, shorter.GetInvalidationQueues().Distributed);

        var floor = await first.EnsureRetentionContractAsync(prefix + "retention", TimeSpan.FromDays(2));
        Assert.Equal(TimeSpan.FromDays(2), floor);
        Assert.Equal(floor, await second.EnsureRetentionContractAsync(prefix + "retention", TimeSpan.FromDays(3)));
        await Assert.ThrowsAsync<IncompatibleDistributedScopeException>(() =>
            second.EnsureRetentionContractAsync(prefix + "retention", TimeSpan.FromDays(1)).AsTask());
    }
}
