using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Storage;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class StaleContentTests
{
    [Fact]
    public async Task StaleWhileRevalidate_ServesCachedContent_WhenEnabled()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            EnableStaleWhileRevalidate = true,
            AddDiagnosticHeaders = true,
            DefaultMaxAge = null // Don't use default max-age
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        // Initial response with stale-while-revalidate (expired immediately)
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-10);
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("original content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5), // 5 minutes max-age
                    Extensions = { new NameValueHeaderValue("stale-while-revalidate", "300") }
                },
                Date = pastDate // Make it already stale
            }
        };

        // Revalidation response
        var revalidationResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("updated content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) }
            }
        };

        innerHandler.SetResponses(initialResponse, revalidationResponse);

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/data");
        var content1 = await response1.Content.ReadAsStringAsync();

        var response2 = await httpClient.GetAsync("https://api.example.com/data");
        var content2 = await response2.Content.ReadAsStringAsync();

        // Wait a bit for potential background revalidation
        await Task.Delay(100);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("original content", content1);
        Assert.Equal("original content", content2); // Should serve stale content immediately

        // Check for stale-while-revalidate header
        Assert.Equal("STALE-WHILE-REVALIDATE", response2.Headers.GetValues("X-Cache").FirstOrDefault());

        // Should have made both requests (initial + background revalidation)
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task StaleIfError_ServesCachedContent_WhenRequestFails()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            EnableStaleIfError = true,
            MaxStaleIfError = TimeSpan.FromHours(1),
            AddDiagnosticHeaders = true
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        // Initial successful response (already stale)
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-10);
        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("cached content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMinutes(5),
                    Extensions = { new NameValueHeaderValue("stale-if-error", "3600") }
                },
                Date = pastDate // Make it already stale
            }
        };

        innerHandler.SetResponse(initialResponse);

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/data");
        var content1 = await response1.Content.ReadAsStringAsync();

        // Content is already stale due to past Date header

        // Configure handler to throw exception on next request
        innerHandler.ShouldThrowException = true;

        var response2 = await httpClient.GetAsync("https://api.example.com/data");
        var content2 = await response2.Content.ReadAsStringAsync();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal("cached content", content1);
        Assert.Equal("cached content", content2); // Should serve stale content due to error

        // Check for stale-if-error header
        Assert.Equal("STALE-IF-ERROR", response2.Headers.GetValues("X-Cache").FirstOrDefault());

        // Should have attempted both requests
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task StaleWhileRevalidate_Disabled_DoesNotServeStaleContent()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            EnableStaleWhileRevalidate = false, // Disabled
            DefaultMaxAge = TimeSpan.FromMilliseconds(1)
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
            Content = new StringContent("content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue
                {
                    MaxAge = TimeSpan.FromMilliseconds(1),
                    Extensions = { new NameValueHeaderValue("stale-while-revalidate", "300") }
                }
            }
        };

        innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/data");

        // Wait for content to become stale
        await Task.Delay(10);

        var response2 = await httpClient.GetAsync("https://api.example.com/data");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        // Should make 2 requests since stale-while-revalidate is disabled
        Assert.Equal(2, innerHandler.RequestCount);
    }

    [Fact]
    public async Task StaleIfError_Disabled_ThrowsException_WhenRequestFails()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            EnableStaleIfError = false // Disabled
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
            Content = new StringContent("content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(1) }
            }
        };

        innerHandler.SetResponse(response);

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/data");

        // Wait for content to become stale
        await Task.Delay(1100);

        // Configure handler to throw exception
        innerHandler.ShouldThrowException = true;

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await httpClient.GetAsync("https://api.example.com/data");
        });
    }

    [Fact]
    public async Task StaleIfError_WithinTimeLimit_ServesCachedContent()
    {
        // Arrange
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions
        {
            EnableStaleIfError = true,
            MaxStaleIfError = TimeSpan.FromSeconds(30), // 30 second limit
            AddDiagnosticHeaders = true
        };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        // Response that's already stale
        var pastDate = DateTimeOffset.UtcNow.AddMinutes(-10);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("stale content"),
            Headers = {
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) },
                Date = pastDate // Make it already stale
            }
        };

        innerHandler.SetResponse(response);

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/data");

        // Content is already stale (but within stale-if-error limit)

        innerHandler.ShouldThrowException = true;

        var response2 = await httpClient.GetAsync("https://api.example.com/data");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        var content = await response2.Content.ReadAsStringAsync();
        Assert.Equal("stale content", content);
        Assert.Equal("STALE-IF-ERROR", response2.Headers.GetValues("X-Cache").FirstOrDefault());
    }
}