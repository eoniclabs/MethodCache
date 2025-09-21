using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Storage;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.UnitTests;

public class PrivateSharedCacheTests
{
    [Fact]
    public async Task PrivateCache_CachesPrivateResponses()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            IsSharedCache = false, // Private cache
            AddDiagnosticHeaders = true
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("private content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    Private = true,
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/private");
        var response2 = await httpClient.GetAsync("https://api.example.com/private");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Private cache should cache private responses
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("FRESH", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }

    [Fact]
    public async Task SharedCache_DoesNotCachePrivateResponses()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            IsSharedCache = true, // Shared cache
            AddDiagnosticHeaders = true
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("private content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    Private = true,
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/private");
        var response2 = await httpClient.GetAsync("https://api.example.com/private");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Shared cache should NOT cache private responses
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task SharedCache_UsesSMaxAge_OverMaxAge()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            IsSharedCache = true,
            AddDiagnosticHeaders = true
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("shared content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(10), // Client max-age
                    SharedMaxAge = TimeSpan.FromMinutes(2) // Shared cache max-age (should take precedence)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/shared");
        var response2 = await httpClient.GetAsync("https://api.example.com/shared");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Should cache with s-maxage (2 minutes)
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("FRESH", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }

    [Fact]
    public async Task PrivateCache_IgnoresSMaxAge_UsesMaxAge()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            IsSharedCache = false, // Private cache
            AddDiagnosticHeaders = true
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("content for private cache"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(10), // Private cache should use this
                    SharedMaxAge = TimeSpan.FromSeconds(1) // Private cache should ignore this
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/content");
        var response2 = await httpClient.GetAsync("https://api.example.com/content");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Private cache should use max-age (10 minutes), not s-maxage (1 second)
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("FRESH", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }

    [Fact]
    public async Task SharedCache_CachesPublicResponses()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            IsSharedCache = true,
            AddDiagnosticHeaders = true
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("public content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    Public = true,
                    MaxAge = TimeSpan.FromMinutes(5)
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/public");
        var response2 = await httpClient.GetAsync("https://api.example.com/public");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Should cache public responses
        Assert.Equal(1, innerHandler.RequestCount);
        Assert.Equal("FRESH", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }
}