using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace MethodCache.AspNetCore.Tests;

public class HealthCapabilityTests
{
    [Fact]
    public async Task ConfiguredFailureStatus_IsRespectedForCacheDegradation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCacheDistributedCache(_ => new FailingCache());
        services.AddHealthChecks().AddMethodCache(failureStatus: HealthStatus.Unhealthy);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Theory]
    [InlineData((int)BackplaneState.Unknown, "unknown")]
    [InlineData((int)BackplaneState.Connecting, "connecting")]
    [InlineData((int)BackplaneState.Disconnected, "disconnected")]
    public async Task HealthyL2_DoesNotHideAnUnestablishedBackplane(int state, string expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        services.AddKeyedSingleton<IBackplane>("MethodCache", new FixedBackplane((BackplaneState)state));
        services.AddHealthChecks().AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        var data = Assert.Single(result.Entries).Value.Data;
        Assert.Equal(true, data["distributedCacheAvailable"]);
        Assert.Equal(expected, data["backplaneStatus"]);
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task PublicationQueue_IsDistinctFromStorageQueue_AndDegradesHealth()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        services.AddKeyedSingleton<IBackplane>("MethodCache", new FixedBackplane(BackplaneState.Connected, failPublish: true));
        services.AddHealthChecks().AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var removed = await provider.GetRequiredService<ICacheManager>().InvalidateKeysAsync(["key"]);
        Assert.Equal(1, removed.Distributed);
        Assert.Equal(1, removed.PublicationQueued);
        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        var data = Assert.Single(result.Entries).Value.Data;
        Assert.Equal(0, data["invalidationQueued"]);
        Assert.Equal(1, data["publicationQueued"]);
        Assert.Equal(true, data["distributedCacheAvailable"]);
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Theory]
    [InlineData(false, "noop")]
    [InlineData(true, "custom")]
    public async Task UnsupportedManagerStatistics_AreReportedAsCapabilities(bool custom, string capability)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICacheManager>(custom ? new CustomManager() : new NoOpCacheManager());
        services.AddMethodCache();
        services.AddHealthChecks().AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        var data = Assert.Single(result.Entries).Value.Data;
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(capability, data["managerCapability"]);
        Assert.Equal(false, data["statisticsAvailable"]);
        Assert.False(data.ContainsKey("localMethodEntries"));
        Assert.False(data.ContainsKey("totalWeight"));
    }

    [Fact]
    public async Task NoOpWithHttp_ReportsHttpStatisticsSeparatelyFromUnsupportedMethodStatistics()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<ICacheManager, NoOpCacheManager>(); services.AddMethodCache();
        services.AddHttpClient("api").AddHttpCaching(o => o.AllowUnverifiedTransport = true)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpOrigin());
        services.AddHealthChecks().AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        await client.GetStringAsync("https://example.test/a");
        var result = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        var data = Assert.Single(result.Entries).Value.Data;
        Assert.Equal(HealthStatus.Healthy, result.Status); Assert.Equal("noop", data["managerCapability"]);
        Assert.Equal(true, data["statisticsAvailable"]); Assert.Equal(false, data["methodStatisticsAvailable"]);
        Assert.Equal("local", data["httpCapability"]); Assert.Equal(1L, data["localHttpEntries"]);
        Assert.False(data.ContainsKey("localMethodEntries")); Assert.False(data.ContainsKey("totalWeight"));
    }

    private sealed class HttpOrigin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new StringContent("value"), Headers = { CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) } } });
    }

    private sealed class CustomManager : ICacheManager
    {
        private readonly NoOpCacheManager _inner = new();
        public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory, CacheEntryOptions? options, CancellationToken cancellationToken) => _inner.GetOrCreateAsync(key, factory, options, cancellationToken);
        public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken) => _inner.TryGetAsync<T>(key, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken) => _inner.InvalidateKeysAsync(keys, cancellationToken);
        public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken) => _inner.InvalidateTagsAsync(tags, cancellationToken);
    }

    private sealed class FixedBackplane(BackplaneState state, bool failPublish = false) : IBackplane
    {
        public BackplaneState ConnectionState => state;
        public string InstanceId => "health-test";
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => failPublish ? Task.FromException(new InvalidOperationException("offline")) : Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => PublishInvalidationAsync(tag, cancellationToken);
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FailingCache : IAtomicInvalidationStore
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("offline");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("offline");
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => throw new InvalidOperationException("offline");
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("offline");
        public void Refresh(string key) => throw new InvalidOperationException("offline");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("offline");
        public void Remove(string key) => throw new InvalidOperationException("offline");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("offline");
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default) => throw new InvalidOperationException("offline");
    }
}
