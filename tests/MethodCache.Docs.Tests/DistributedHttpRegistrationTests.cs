using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class DistributedHttpRegistrationTests
{
    [Fact]
    public async Task ApplicationCache_WithoutOwnedTransport_IsNeverAnHttpFallback()
    {
        var services = new ServiceCollection(); var application = new RecordingCache();
        services.AddSingleton<IDistributedCache>(application); services.AddMethodCache();
        services.AddHttpClient("selected").ConfigurePrimaryHttpMessageHandler(() => new Origin()).AddHttpCaching(o => o.AllowUnverifiedTransport = true);
        await using var provider = services.BuildServiceProvider(); using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("selected");
        await client.GetStringAsync("https://api.example.com/a"); await client.GetStringAsync("https://api.example.com/a");
        Assert.Same(application, provider.GetRequiredService<IDistributedCache>()); Assert.Equal(0, application.Reads);
        Assert.Equal(1, (await provider.GetRequiredService<ICacheAdministrator>().GetStatsAsync()).LocalHttpEntries);
    }

    [Fact]
    public void CustomMethodTransport_WithoutNativeEpochs_CannotSilentlyBecomeHttpStorage()
    {
        var services = new ServiceCollection(); services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        services.AddHttpClient("unsupported").AddHttpCaching(); using var provider = services.BuildServiceProvider();
        Assert.Throws<NotSupportedException>(() => provider.GetRequiredService<IHttpClientFactory>().CreateClient("unsupported"));
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
