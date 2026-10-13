using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

// The header promises in docs/features/http-caching.md: unparsable Cache-Control and Vary fields, the one-year cap,
// weak ETags, transport-added Accept-Encoding and the URL fragment.
public class HttpResponseHeaderTests
{
    private const string Url = "https://api.example.com/item";

    [Theory]
    [InlineData("Cache-Control", "private; max-age=600")]
    [InlineData("Vary", "Accept-Language; q")]
    public async Task UnparsableFieldNextToAValidOne_IsNotStored(string header, string invalid)
    {
        using var origin = new Origin { ExtraHeader = (header, invalid) };
        using var provider = Provider(origin);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        await client.GetStringAsync(Url);
        await client.GetStringAsync(Url);

        Assert.Equal(2, origin.Requests);
    }

    [Fact]
    public async Task FarFutureExpires_IsKeptAtMostAYear()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        using var origin = new Origin { MaxAge = null, Expires = DateTimeOffset.MaxValue.AddDays(-1) };
        using var provider = Provider(origin, time);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        await client.GetStringAsync(Url);
        time.Advance(TimeSpan.FromDays(364));
        await client.GetStringAsync(Url);
        Assert.Equal(1, origin.Requests);
        time.Advance(TimeSpan.FromDays(2));
        await client.GetStringAsync(Url);
        Assert.Equal(2, origin.Requests);
    }

    [Fact]
    public async Task WeakETag_IsSentBackAsAWeakValidator()
    {
        using var origin = new Origin { MaxAge = TimeSpan.Zero, ETag = new EntityTagHeaderValue("\"abc\"", isWeak: true) };
        using var provider = Provider(origin);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        await client.GetStringAsync(Url);
        Assert.Equal("v1", await client.GetStringAsync(Url));

        Assert.Equal("W/\"abc\"", origin.LastIfNoneMatch);
    }

    [Fact]
    public async Task VaryAcceptEncoding_WorksWithHeadersTheTransportAdds()
    {
        using var origin = new Origin { AddAcceptEncoding = true, Vary = "Accept-Encoding" };
        using var provider = Provider(origin);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        await client.GetStringAsync(Url);
        await client.GetStringAsync(Url);

        Assert.Equal(1, origin.Requests);
    }

    [Fact]
    public async Task Fragment_IsNotPartOfTheKey()
    {
        using var origin = new Origin();
        using var provider = Provider(origin);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");

        await client.GetStringAsync(Url);
        await client.GetStringAsync(Url + "#top");

        Assert.Equal(1, origin.Requests);
    }

    private static ServiceProvider Provider(Origin origin, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        if (time != null) services.AddSingleton(time);
        services.AddHttpClient("api").ConfigurePrimaryHttpMessageHandler(() => origin).AddHttpCaching(options => options.AllowUnverifiedTransport = true);
        return services.BuildServiceProvider();
    }

    private sealed class Origin : HttpMessageHandler
    {
        private int _requests;
        public TimeSpan? MaxAge { get; set; } = TimeSpan.FromMinutes(5);
        public DateTimeOffset? Expires { get; set; }
        public EntityTagHeaderValue? ETag { get; set; }
        public string? Vary { get; set; }
        public (string Name, string Value)? ExtraHeader { get; set; }
        public bool AddAcceptEncoding { get; set; }
        public string? LastIfNoneMatch { get; private set; }
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            // What SocketsHttpHandler does with AutomaticDecompression
            if (AddAcceptEncoding) request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            LastIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            if (ETag != null && LastIfNoneMatch == ETag.ToString())
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified) { Headers = { ETag = ETag } });
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1") };
            if (MaxAge != null) response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = MaxAge, Public = true };
            if (Expires != null) response.Content.Headers.Expires = Expires;
            if (ETag != null) response.Headers.ETag = ETag;
            if (Vary != null) response.Headers.Vary.Add(Vary);
            if (ExtraHeader is { } extra)
            {
                // A valid field first, then the one that doesn't parse
                response.Headers.TryAddWithoutValidation(extra.Name, extra.Name == "Vary" ? "Accept" : "public, max-age=600");
                response.Headers.TryAddWithoutValidation(extra.Name, extra.Value);
            }
            return Task.FromResult(response);
        }
    }
}
