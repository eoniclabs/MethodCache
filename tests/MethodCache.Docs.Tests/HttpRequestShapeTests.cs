using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

// The promises in docs/features/http-caching.md "Redirects, retries and request bodies" and the
// ConfigureHttpClientDefaults note.
public class HttpRequestShapeTests
{
    private const string Url = "https://api.example.com/item";

    [Fact]
    public async Task FollowedRedirect_IsNotStoredUnderTheRequestedUrl()
    {
        using var origin = new Origin { RedirectTo = new Uri("https://api.example.com/elsewhere") };
        using var provider = Provider(origin);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        Assert.Equal("v1 /elsewhere", await client.GetStringAsync(Url));
        origin.Version = "v2";
        Assert.Equal("v2 /elsewhere", await client.GetStringAsync(Url));
    }

    [Fact]
    public async Task UrlThatStartsRedirecting_StillGetsAFullResponse()
    {
        using var origin = new Origin { MaxAge = TimeSpan.Zero, ETag = "\"v1\"" };
        using var provider = Provider(origin);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        Assert.Equal("v1 /item", await client.GetStringAsync(Url));

        // The redirect target has the same ETag, so it answers the cache's validators with 304
        origin.RedirectTo = new Uri("https://api.example.com/elsewhere");
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("v1 /elsewhere", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RetriedMessage_GetsAFullResponse()
    {
        using var origin = new Origin { MaxAge = TimeSpan.Zero, ETag = "\"v1\"" };
        var services = new ServiceCollection();
        services.AddHttpClient("api")
            .ConfigurePrimaryHttpMessageHandler(() => origin)
            .AddHttpMessageHandler(() => new SendTwice())
            .AddHttpCaching(options => options.AllowUnverifiedTransport = true);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        using (await client.GetAsync(Url)) { }

        // Revalidated twice through the same message, like a retry handler does
        using var response = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("v1 /item", await response.Content.ReadAsStringAsync());
    }

    private sealed class SendTwice : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            (await base.SendAsync(request, cancellationToken)).Dispose();
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task GetWithBody_GoesToTheOrigin()
    {
        using var origin = new Origin();
        using var provider = Provider(origin);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        for (var i = 0; i < 2; i++)
        {
            using var search = new HttpRequestMessage(HttpMethod.Get, Url) { Content = new StringContent("{\"query\":" + i + "}") };
            using (await client.SendAsync(search)) { }
        }

        Assert.Equal(2, origin.Requests);
    }

    [Fact]
    public async Task ConfigureHttpClientDefaults_KeepsEachClientsEntriesApart()
    {
        using var first = new Origin { Version = "first" };
        using var second = new Origin { Version = "second" };
        var services = new ServiceCollection();
        services.ConfigureHttpClientDefaults(builder => builder.AddHttpCaching(options => options.AllowUnverifiedTransport = true));
        services.AddHttpClient("first").ConfigurePrimaryHttpMessageHandler(() => first);
        services.AddHttpClient("second").ConfigurePrimaryHttpMessageHandler(() => second);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var firstClient = factory.CreateClient("first");
        using var secondClient = factory.CreateClient("second");

        Assert.Equal("first /item", await firstClient.GetStringAsync(Url));
        Assert.Equal("second /item", await secondClient.GetStringAsync(Url));
        Assert.Equal("first /item", await firstClient.GetStringAsync(Url));
        Assert.Equal(1, first.Requests);
    }

    private static ServiceProvider Provider(Origin origin)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => origin).AddHttpCaching(options => options.AllowUnverifiedTransport = true);
        return services.BuildServiceProvider();
    }

    private sealed class Origin : HttpMessageHandler
    {
        private int _requests;
        public string Version { get; set; } = "v1";
        public string? ETag { get; set; }
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(5);
        public Uri? RedirectTo { get; set; }
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            // What SocketsHttpHandler does when it follows a redirect
            if (RedirectTo != null) request.RequestUri = RedirectTo;
            if (ETag != null && request.Headers.IfNoneMatch.Any(tag => tag.Tag == ETag))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified) { Headers = { ETag = new EntityTagHeaderValue(ETag) } });
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Version + " " + request.RequestUri!.AbsolutePath),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = MaxAge } }
            };
            if (ETag != null) response.Headers.ETag = new EntityTagHeaderValue(ETag);
            return Task.FromResult(response);
        }
    }
}
