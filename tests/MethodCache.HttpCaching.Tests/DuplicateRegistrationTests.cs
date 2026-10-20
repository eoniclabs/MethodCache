using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

/// <summary>
/// AddHttpCaching for every client (ConfigureHttpClientDefaults) and again for one client, or twice for one client,
/// still gives that client's pipeline one cache: a second cache inside the first would cache, count and report every
/// response twice.
/// </summary>
public class DuplicateRegistrationTests
{
    private const string Url = "https://api.example.com/orders/1";

    [Theory]
    [InlineData(true)]  // ConfigureHttpClientDefaults first, as it usually is
    [InlineData(false)] // the named client first
    public async Task CachingForEveryClient_AndForOneClient_AddsOneCacheToThatClient(bool defaultsFirst)
    {
        var origin = new CountingOrigin();
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.ConfigureAll<HttpCacheOptions>(options => options.AllowUnverifiedTransport = true);
        if (defaultsFirst) services.ConfigureHttpClientDefaults(client => client.AddHttpCaching());
        services.AddHttpClient("orders").AddHttpCaching(options => options.AddDiagnosticHeaders = true)
            .ConfigurePrimaryHttpMessageHandler(() => origin);
        if (!defaultsFirst) services.ConfigureHttpClientDefaults(client => client.AddHttpCaching());
        using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        using var miss = await client.GetAsync(Url);
        using var hit = await client.GetAsync(Url);

        Assert.Equal(1, CacheHandlers(provider, "orders"));
        Assert.Equal(1, origin.Requests);
        Assert.Equal("MethodCache; fwd=uri-miss; fwd-status=200; stored", Assert.Single(miss.Headers.GetValues("Cache-Status")));
        Assert.Matches(HttpCacheTestFactory.FreshHit, Assert.Single(hit.Headers.GetValues("Cache-Status")));
    }

    [Fact]
    public void CachingAddedTwiceToOneClient_AddsOneCache_AndAppliesBothConfigurations()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddHttpClient("orders")
            .AddHttpCaching(options => options.EnableStaleIfError = true)
            .AddHttpCaching(options => options.AddDiagnosticHeaders = true);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<HttpCacheOptions>>().Get("orders");

        Assert.Equal(1, CacheHandlers(provider, "orders"));
        Assert.True(options.EnableStaleIfError);
        Assert.True(options.AddDiagnosticHeaders);
    }

    [Fact]
    public async Task CachingForEveryClient_TakesOneClientsOptions_FromItsNamedConfiguration()
    {
        var origin = new CountingOrigin();
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.ConfigureAll<HttpCacheOptions>(options => options.AllowUnverifiedTransport = true);
        services.ConfigureHttpClientDefaults(client => client.AddHttpCaching().ConfigurePrimaryHttpMessageHandler(() => origin));
        services.Configure<HttpCacheOptions>("orders", options => options.AddDiagnosticHeaders = true);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        using var orders = await factory.CreateClient("orders").GetAsync(Url);
        using var users = await factory.CreateClient("users").GetAsync(Url);

        Assert.Equal("MethodCache; fwd=uri-miss; fwd-status=200; stored", Assert.Single(orders.Headers.GetValues("Cache-Status")));
        Assert.False(users.Headers.Contains("Cache-Status"));
        Assert.Equal(1, CacheHandlers(provider, "users"));
    }

    private static int CacheHandlers(IServiceProvider provider, string client)
    {
        HttpMessageHandler? handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(client);
        var caches = 0;
        while (handler is DelegatingHandler delegating)
        {
            if (delegating is HttpCacheHandler) caches++;
            handler = delegating.InnerHandler;
        }

        return caches;
    }

    private sealed class CountingOrigin : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("order"), RequestMessage = request };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
            return Task.FromResult(response);
        }
    }
}
