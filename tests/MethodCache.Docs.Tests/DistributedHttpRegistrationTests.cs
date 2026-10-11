using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class DistributedHttpRegistrationTests
{
    [Theory]
    [InlineData("redis")]
    [InlineData("sql")]
    [InlineData("factory")]
    public void OwnedDistributedCache_CreateClient_DoesNotRequireAnApplicationCache(string providerName)
    {
        var services = new ServiceCollection();
        switch (providerName)
        {
            case "redis":
                services.AddMethodCacheRedis("localhost:6379");
                break;
            case "sql":
                services.AddMethodCacheSqlServer("Server=localhost;Database=MethodCache;Integrated Security=true;TrustServerCertificate=true");
                break;
            default:
                services.AddMethodCacheDistributedCache(_ => new RecordingCache());
                break;
        }
        services.Configure<HttpCacheStorageOptions>(options => options.UseDistributedCache = true);
        services.AddHttpClient("owned").AddHttpCaching();
        using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("owned");
        Assert.Null(provider.GetService<IDistributedCache>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DistributedStorage_PrefersMethodCacheOwnedCache_AndFallsBackToApplicationCache(bool ownCacheRegistered)
    {
        var services = new ServiceCollection();
        var applicationCache = new RecordingCache();
        var ownedCache = new RecordingCache();
        services.AddSingleton<IDistributedCache>(applicationCache);
        if (ownCacheRegistered)
        {
            services.AddMethodCacheDistributedCache(_ => ownedCache);
        }
        services.Configure<HttpCacheStorageOptions>(options => options.UseDistributedCache = true);
        services.AddHttpClient("selected").ConfigurePrimaryHttpMessageHandler(() => new Origin()).AddHttpCaching();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("selected");
        using var response = await client.GetAsync("https://api.example.com/distributed");

        Assert.Same(applicationCache, provider.GetRequiredService<IDistributedCache>());
        Assert.True((ownCacheRegistered ? ownedCache : applicationCache).Reads > 0);
        Assert.Equal(0, (ownCacheRegistered ? applicationCache : ownedCache).Reads);
    }

    private sealed class Origin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("value"),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) } }
            });
    }

    private sealed class RecordingCache : IDistributedCache
    {
        public int Reads { get; private set; }
        public byte[]? Get(string key) { Reads++; return null; }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Task.CompletedTask;
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
