using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.HttpCaching.IntegrationTests;

public class HttpCachingRegistrationTests
{
    [Fact]
    public void AddHttpCaching_LeavesTheAppsMemoryCacheAlone()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("api").AddHttpCaching();
        services.AddMemoryCache();

        var memoryCache = services.BuildServiceProvider().GetRequiredService<IMemoryCache>();

        // Would throw "Cache entry must specify a value for Size" if HTTP caching had registered a size-limited cache
        memoryCache.Set("app-key", 42);
        Assert.Equal(42, memoryCache.Get<int>("app-key"));
    }

    [Fact]
    public void AddHttpCaching_DoesNotRegisterAnIMemoryCache()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("api").AddHttpCaching();

        Assert.Null(services.BuildServiceProvider().GetService<IMemoryCache>());
    }

    [Fact]
    public void EachHttpClient_GetsItsOwnOptions()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("fast").AddHttpCaching(o => o.DefaultMaxAge = TimeSpan.FromMinutes(1));
        services.AddHttpClient("slow").AddHttpCaching(o => o.DefaultMaxAge = TimeSpan.FromHours(1));

        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<HttpCacheOptions>>();

        Assert.Equal(TimeSpan.FromMinutes(1), monitor.Get("fast").DefaultMaxAge);
        Assert.Equal(TimeSpan.FromHours(1), monitor.Get("slow").DefaultMaxAge);
    }

    [Fact]
    public async Task EachHttpClient_HasItsOwnEntries()
    {
        var origin = new CountingOrigin();
        var services = new ServiceCollection();
        services.AddHttpClient("a").AddHttpCaching().ConfigurePrimaryHttpMessageHandler(() => origin);
        services.AddHttpClient("b").AddHttpCaching().ConfigurePrimaryHttpMessageHandler(() => origin);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        await factory.CreateClient("a").GetStringAsync("https://api.example.com/x");
        await factory.CreateClient("a").GetStringAsync("https://api.example.com/x");
        await factory.CreateClient("b").GetStringAsync("https://api.example.com/x");

        Assert.Equal(2, origin.Requests);
    }

    [Fact]
    public async Task PostThroughTheClient_InvalidatesTheCachedGet()
    {
        var origin = new CountingOrigin();
        var services = new ServiceCollection();
        services.AddHttpClient("api", c => c.BaseAddress = new Uri("https://api.example.com/"))
            .AddHttpCaching()
            .ConfigurePrimaryHttpMessageHandler(() => origin);
        var client = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>().CreateClient("api");

        Assert.Equal("1", await client.GetStringAsync("items/1"));
        await client.PostAsync("items/1", new StringContent("x"));

        Assert.Equal("3", await client.GetStringAsync("items/1"));
    }

    private sealed class CountingOrigin : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _requests);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(count.ToString()) };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
            return Task.FromResult(response);
        }
    }
}
