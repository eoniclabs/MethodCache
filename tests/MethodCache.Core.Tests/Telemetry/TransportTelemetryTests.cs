using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Telemetry;

public class TransportTelemetryTests
{
    [Fact]
    public async Task TransportOperations_AreMeasuredIndividually_AndMemoryHitsDoNoTransportWork()
    {
        using var provider = Node(new MethodCache.TestSupport.AtomicTestCache());
        using var metrics = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateAsync("user:secret-key", _ => ValueTask.FromResult(7));
        var count = metrics.Distributed.Count;
        Assert.True(count >= 3);
        Assert.Contains(metrics.Distributed, row => row.Operation == "set");
        Assert.Equal(7, await cache.GetOrCreateAsync<int>("user:secret-key", _ => throw new InvalidOperationException("Expected hit")));
        Assert.Equal(count, metrics.Distributed.Count);
        await cache.InvalidateTagsAsync(["user:secret-tag"]);
        Assert.Contains(metrics.Distributed, row => row.Operation == "invalidate");
        Assert.All(metrics.Distributed, row =>
        {
            Assert.Contains(row.Operation, new[] { "get", "set", "remove", "invalidate" });
            Assert.All(row.Tags.Keys, key => Assert.Contains(key, new[] { "methodcache.operation", "error.type" }));
        });
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.invalidations" && Equals(row.Tags["methodcache.invalidation.stage"], "distributed") && Equals(row.Tags["methodcache.invalidation.type"], "tag") && row.Value == 1);
    }

    [Fact]
    public async Task PublicationRecovery_HasItsOwnGaugeAndStageCounters()
    {
        var backplane = new FailingPublication();
        using var provider = Node(new MethodCache.TestSupport.AtomicTestCache(), backplane);
        using var metrics = new TelemetryRecorder(provider);
        var result = await provider.GetRequiredService<ICacheManager>().InvalidateKeysAsync(["key"]);
        Assert.Equal(1, result.Distributed);
        Assert.Equal(1, result.PublicationQueued);
        metrics.Poll();
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.distributed.queue" && Equals(row.Tags["methodcache.invalidation.stage"], "publication") && row.Value == 1);
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.distributed.queue" && Equals(row.Tags["methodcache.invalidation.stage"], "storage") && row.Value == 0);
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.invalidations" && Equals(row.Tags["methodcache.invalidation.stage"], "publication_queued") && row.Value == 1);
        Assert.Contains(metrics.Distributed, row => row.Operation == "invalidate" && row.Tags.ContainsKey("error.type"));
        backplane.Fail = false;
        await provider.GetRequiredService<DistributedCacheLayer>().FlushPublicationsAsync();
        Assert.Equal(0, provider.GetRequiredService<ICacheAdministrator>().GetInvalidationQueues().PublicationQueued);
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.invalidations" && Equals(row.Tags["methodcache.invalidation.stage"], "published") && row.Value == 1);
    }

    [Fact]
    public async Task StorageRecovery_IsNotReportedAsPublicationRecovery()
    {
        using var provider = Node(new FailingStamp());
        using var metrics = new TelemetryRecorder(provider);
        var result = await provider.GetRequiredService<ICacheManager>().InvalidateKeysAsync(["key"]);
        Assert.Equal(1, result.Queued);
        Assert.Equal(0, result.Distributed);
        metrics.Poll();
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.distributed.queue" && Equals(row.Tags["methodcache.invalidation.stage"], "storage") && row.Value == 1);
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.distributed.queue" && Equals(row.Tags["methodcache.invalidation.stage"], "publication") && row.Value == 0);
        Assert.Contains(metrics.Longs, row => row.Name == "methodcache.invalidations" && Equals(row.Tags["methodcache.invalidation.stage"], "queued") && row.Value == 1);
    }

    private static ServiceProvider Node(IDistributedCache cache, IBackplane? backplane = null)
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => cache);
        if (backplane != null) services.AddKeyedSingleton("MethodCache", backplane);
        return services.BuildServiceProvider();
    }

    private sealed class FailingPublication : IBackplane
    {
        internal bool Fail = true;
        public BackplaneState ConnectionState => BackplaneState.Connected;
        public string InstanceId => "test";
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => Fail ? Task.FromException(new InvalidOperationException("offline")) : Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => PublishInvalidationAsync(tag, cancellationToken);
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailingStamp : IAtomicInvalidationStore
    {
        private readonly MethodCache.TestSupport.AtomicTestCache _inner = new();
        public byte[]? Get(string key) => _inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(key, value, options, token);
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default) => throw new InvalidOperationException("offline");
    }
}
