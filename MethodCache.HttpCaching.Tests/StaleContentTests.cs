using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Options;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class StaleContentTests
{
    [Fact]
    public async Task StaleWhileRevalidate_ServesCachedContent_WhenEnabled()
    {
        var options = new HttpCacheOptions();
        options.Behavior.EnableStaleWhileRevalidate = true;
        options.Diagnostics.AddDiagnosticHeaders = true;
        options.Freshness.DefaultMaxAge = null;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-10);
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("original content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5),
                    Extensions = { new NameValueHeaderValue("stale-while-revalidate", "300") }
                },
                Date = pastDate
            }
        };

        var revalidationResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("updated content"),
            Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) } }
        };

        innerHandler.SetResponses(initialResponse, revalidationResponse);

        var response1 = await client.GetAsync("https://api.example.com/data");
        var content1 = await response1.Content.ReadAsStringAsync();

        var response2 = await client.GetAsync("https://api.example.com/data");
        var content2 = await response2.Content.ReadAsStringAsync();

        await Task.Delay(100);

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("original content", content1);
        Assert.Equal("original content", content2);
        Assert.Equal("STALE", response2.Headers.GetValues("X-Cache").FirstOrDefault());
        Assert.True(innerHandler.RequestCount >= 2);
    }

    [Fact]
    public async Task StaleIfError_ServesCachedContent_WhenRequestFails()
    {
        var options = new HttpCacheOptions();
        options.Behavior.EnableStaleIfError = true;
        options.Behavior.MaxStaleIfError = TimeSpan.FromHours(1);
        options.Diagnostics.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-10);
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5),
                    Extensions = { new NameValueHeaderValue("stale-if-error", "3600") }
                },
                Date = pastDate
            }
        };

        innerHandler.SetResponse(initialResponse);

        var response1 = await client.GetAsync("https://api.example.com/data");
        var content1 = await response1.Content.ReadAsStringAsync();

        innerHandler.ShouldThrowException = true;

        var response2 = await client.GetAsync("https://api.example.com/data");
        var content2 = await response2.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal("cached content", content1);
        Assert.Equal("cached content", content2);
        Assert.Equal("STALE-IF-ERROR", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }

    [Fact]
    public async Task StaleWhileRevalidate_Disabled_MakesOriginRequest()
    {
        var options = new HttpCacheOptions();
        options.Behavior.EnableStaleWhileRevalidate = false;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMilliseconds(1),
                    Extensions = { new NameValueHeaderValue("stale-while-revalidate", "300") }
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/data");
        await Task.Delay(10);
        var response2 = await client.GetAsync("https://api.example.com/data");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task StaleIfError_Disabled_ThrowsException_WhenRequestFails()
    {
        var options = new HttpCacheOptions();
        options.Behavior.EnableStaleIfError = false;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content"),
            Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) } }
        };

        innerHandler.SetResponse(response);

        var response1 = await client.GetAsync("https://api.example.com/data");
        await Task.Delay(1100);
        innerHandler.ShouldThrowException = true;

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.example.com/data"));
    }

    [Fact]
    public async Task StaleIfError_WithinTimeLimit_ServesCachedContent()
    {
        var options = new HttpCacheOptions();
        options.Behavior.EnableStaleIfError = true;
        options.Behavior.MaxStaleIfError = TimeSpan.FromSeconds(30);
        options.Diagnostics.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-10);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("stale content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) },
                Date = pastDate
            }
        };

        innerHandler.SetResponse(response);

        var response1 = await client.GetAsync("https://api.example.com/data");
        innerHandler.ShouldThrowException = true;
        var response2 = await client.GetAsync("https://api.example.com/data");
        var content = await response2.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("stale content", content);
        Assert.Equal("STALE-IF-ERROR", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }
}



