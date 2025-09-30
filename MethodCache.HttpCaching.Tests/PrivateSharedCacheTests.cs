using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using MethodCache.HttpCaching.Options;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class PrivateSharedCacheTests
{
    [Fact]
    public async Task PrivateCache_CachesPrivateResponses()
    {
        var options = new HttpCacheOptions();
        options.Behavior.IsSharedCache = false;
        options.Diagnostics.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("private content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    Private = true,
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/private");
        var response2 = await client.GetAsync("https://api.example.com/private");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("HIT", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }

    [Fact]
    public async Task SharedCache_DoesNotCachePrivateResponses()
    {
        var options = new HttpCacheOptions();
        options.Behavior.IsSharedCache = true;
        options.Diagnostics.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("private content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    Private = true,
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/private");
        var response2 = await client.GetAsync("https://api.example.com/private");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task SharedCache_UsesSMaxAge_OverMaxAge()
    {
        var options = new HttpCacheOptions();
        options.Behavior.IsSharedCache = true;
        options.Diagnostics.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("shared content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(10),
                    SharedMaxAge = TimeSpan.FromMinutes(2)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/shared");
        var response2 = await client.GetAsync("https://api.example.com/shared");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("HIT", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }

    [Fact]
    public async Task PrivateCache_IgnoresSMaxAge_UsesMaxAge()
    {
        var options = new HttpCacheOptions();
        options.Behavior.IsSharedCache = false;
        options.Diagnostics.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content for private cache"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(10),
                    SharedMaxAge = TimeSpan.FromSeconds(1)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/content");
        var response2 = await client.GetAsync("https://api.example.com/content");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("HIT", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }

    [Fact]
    public async Task SharedCache_CachesPublicResponses()
    {
        var options = new HttpCacheOptions();
        options.Behavior.IsSharedCache = true;
        options.Diagnostics.AddDiagnosticHeaders = true;

        var (client, innerHandler) = HttpCacheTestFactory.CreateClient(options);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("public content"),
            Headers =
            {
                CacheControl = new CacheControlHeaderValue
                {
                    Public = true,
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await client.GetAsync("https://api.example.com/public");
        var response2 = await client.GetAsync("https://api.example.com/public");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("HIT", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }
}

