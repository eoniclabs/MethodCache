using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Tests;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.IntegrationTests;

public class VaryHeaderIntegrationTests
{
    [Fact]
    public async Task CacheRespectVaryHeaders_DifferentAcceptHeaders_StoreSeparateEntries()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions();
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        // Create responses with Vary: Accept header
        var jsonResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"data\": \"json\" }"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        var xmlResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<data>xml</data>"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        innerHandler.SetResponses(jsonResponse, xmlResponse);

        // Act - Request with JSON Accept header
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response1 = await httpClient.SendAsync(request1);

        // Act - Request with XML Accept header
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var response2 = await httpClient.SendAsync(request2);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount); // Both requests hit the origin

        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();

        Assert.Contains("json", content1);
        Assert.Contains("xml", content2);
    }

    [Fact]
    public async Task CacheRespectVaryHeaders_SameAcceptHeader_ServeFromCache()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions();
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"data\": \"json\" }"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act - Two requests with same Accept header
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response1 = await httpClient.SendAsync(request1);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response2 = await httpClient.SendAsync(request2);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount); // Second request served from cache

        var content1 = await response1.Content.ReadAsStringAsync();
        var content2 = await response2.Content.ReadAsStringAsync();

        Assert.Equal(content1, content2);
    }

    [Fact]
    public async Task CacheRespectVaryHeaders_VaryAsterisk_NeverCaches()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions();
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("uncacheable"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "*" }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act - Two identical requests
        var response1 = await httpClient.GetAsync("https://api.example.com/data");
        var response2 = await httpClient.GetAsync("https://api.example.com/data");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount); // Both requests hit origin due to Vary: *
    }

    [Fact]
    public async Task CacheRespectVaryHeaders_MultipleVaryHeaders_WorksCorrectly()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions();
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept", "Accept-Language" }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act - Two requests with same Accept and Accept-Language
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request1.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var response1 = await httpClient.SendAsync(request1);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request2.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var response2 = await httpClient.SendAsync(request2);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount); // Second request served from cache
    }

    [Fact]
    public async Task CacheIgnoreVary_WhenDisabled_IgnoresVaryHeaders()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions { RespectVary = false };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) },
                Vary = { "Accept" }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act - Two requests with different Accept headers
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response1 = await httpClient.SendAsync(request1);

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var response2 = await httpClient.SendAsync(request2);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, innerHandler.RequestCount); // Second request served from cache even with different Accept
    }
}