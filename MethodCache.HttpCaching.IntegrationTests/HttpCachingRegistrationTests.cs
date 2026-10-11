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

    [Fact]
    public async Task UseDistributedCache_SharesETagsBetweenInstances()
    {
        var distributed = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var origin = new ETagOrigin();

        IHttpClientFactory CreateInstance()
        {
            var services = new ServiceCollection();
            services.AddSingleton<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(distributed);
            services.Configure<HttpCacheStorageOptions>(o => o.UseDistributedCache = true);
            services.AddHttpClient("api").AddHttpCaching().ConfigurePrimaryHttpMessageHandler(() => origin);
            return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        }

        await CreateInstance().CreateClient("api").GetStringAsync("https://api.example.com/x");
        var body = await CreateInstance().CreateClient("api").GetStringAsync("https://api.example.com/x");

        Assert.Equal("payload", body);
        Assert.Equal(2, origin.Requests);
        Assert.Equal(1, origin.NotModified);
    }

    [Fact]
    public void UseDistributedCache_WithoutADistributedCache_ExplainsWhatIsMissing()
    {
        var services = new ServiceCollection();
        services.Configure<HttpCacheStorageOptions>(o => o.UseDistributedCache = true);
        services.AddHttpClient("api").AddHttpCaching();

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().GetRequiredService<IHttpCacheStorage>());
        Assert.Contains("IDistributedCache", error.Message);
    }

    [Fact]
    public async Task Requests_AreCountedOnTheMethodCacheMeter()
    {
        var outcomes = new List<string>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "MethodCache" && instrument.Name == "methodcache.http.requests_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome")
                {
                    lock (outcomes)
                    {
                        outcomes.Add((string)tag.Value!);
                    }
                }
            }
        });
        listener.Start();

        var services = new ServiceCollection();
        services.AddHttpClient("metered").AddHttpCaching().ConfigurePrimaryHttpMessageHandler(() => new CountingOrigin());
        services.AddHttpClient("down").AddHttpCaching().ConfigurePrimaryHttpMessageHandler(() => new FailingOrigin());
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("metered");
        await client.GetStringAsync("https://api.example.com/metered");
        await client.GetStringAsync("https://api.example.com/metered");
        await Assert.ThrowsAsync<HttpRequestException>(() => factory.CreateClient("down").GetStringAsync("https://api.example.com/down"));

        Assert.Contains("miss", outcomes);
        Assert.Contains("hit", outcomes);
        Assert.Contains("error", outcomes);
    }

    private sealed class FailingOrigin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("origin down");
    }

    private sealed class ETagOrigin : HttpMessageHandler
    {
        public int Requests;
        public int NotModified;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (request.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\""))
            {
                Interlocked.Increment(ref NotModified);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("payload") };
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
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
