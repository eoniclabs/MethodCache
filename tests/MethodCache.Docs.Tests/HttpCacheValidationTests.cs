using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

public class HttpCacheValidationTests
{
    [Fact]
    public async Task NoCache_WithStaleOptions_ValidatesEveryReuseAndPropagatesErrors()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        var origin = new ValidationOrigin();
        services.AddHttpClient("validated").ConfigurePrimaryHttpMessageHandler(() => origin)
            .AddHttpCaching(options =>
            {
                options.EnableStaleIfError = true;
                options.EnableStaleWhileRevalidate = true;
            });
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("validated");
        using var first = await client.GetAsync("https://api.example.com/validated");
        time.Advance(TimeSpan.FromSeconds(2));
        using var validated = await client.GetAsync("https://api.example.com/validated");
        Assert.Equal("value", await validated.Content.ReadAsStringAsync());
        using var error = await client.GetAsync("https://api.example.com/validated");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal(3, origin.Calls);
    }

    [Fact]
    public async Task Revalidation_NoStore_RemovesThePreviousResponse()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        var origin = new ValidationOrigin(noStoreOnValidation: true);
        services.AddHttpClient("policy").ConfigurePrimaryHttpMessageHandler(() => origin).AddHttpCaching();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("policy");
        using var first = await client.GetAsync("https://api.example.com/policy");
        time.Advance(TimeSpan.FromSeconds(2));
        using var validated = await client.GetAsync("https://api.example.com/policy");
        Assert.Equal("value", await validated.Content.ReadAsStringAsync());
        using var next = await client.GetAsync("https://api.example.com/policy");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, next.StatusCode);
        Assert.Equal(3, origin.Calls);
    }

    private sealed class ValidationOrigin(bool noStoreOnValidation = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 2 || (Calls > 2 && !noStoreOnValidation))
            {
                Assert.Contains(request.Headers.IfNoneMatch, tag => tag.Tag == "\"v1\"");
            }
            var response = new HttpResponseMessage(Calls switch
            {
                1 => HttpStatusCode.OK,
                2 => HttpStatusCode.NotModified,
                _ => HttpStatusCode.ServiceUnavailable
            });
            if (Calls <= 2)
            {
                response.Headers.CacheControl = CacheControlHeaderValue.Parse(Calls == 2 && noStoreOnValidation
                    ? "no-store, max-age=300"
                    : "no-cache, max-age=1, stale-while-revalidate=60, stale-if-error=60");
                response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
                if (Calls == 1)
                {
                    response.Content = new StringContent("value");
                }
            }
            return Task.FromResult(response);
        }
    }
}
