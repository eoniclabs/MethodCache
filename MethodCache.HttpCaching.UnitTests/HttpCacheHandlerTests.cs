using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Storage;
using Xunit;

namespace MethodCache.HttpCaching.UnitTests;

public class HttpCacheHandlerTests
{
    private readonly HttpClient _httpClient;
    private readonly TestHttpMessageHandler _innerHandler;
    private readonly InMemoryHttpCacheStorage _storage;

    public HttpCacheHandlerTests()
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions();
        _storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        _innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(_storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = _innerHandler
        };

        _httpClient = new HttpClient(handler);
    }

    [Fact]
    public async Task Get_Request_CachesResponse_WhenResponseHasCacheControl()
    {
        // Arrange
        var expectedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Hello, World!"),
            Headers = { { "Cache-Control", "max-age=300" } }
        };

        _innerHandler.SetResponses(expectedResponse, expectedResponse);

        // Act
        var response1 = await _httpClient.GetAsync("https://api.example.com/data");
        var response2 = await _httpClient.GetAsync("https://api.example.com/data");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, _innerHandler.RequestCount); // Second request served from cache
    }

    [Fact]
    public async Task Get_Request_Returns304_WhenETagMatches()
    {
        // Arrange - Create a handler with short cache duration for this test
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var options = new HttpCacheOptions { DefaultMaxAge = TimeSpan.FromMilliseconds(1) };
        var storage = new InMemoryHttpCacheStorage(memoryCache, options, NullLogger<InMemoryHttpCacheStorage>.Instance);
        var innerHandler = new TestHttpMessageHandler();

        var handler = new HttpCacheHandler(storage, options, NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };

        var httpClient = new HttpClient(handler);

        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Hello, World!"),
            Headers = {
                ETag = new EntityTagHeaderValue("\"abc123\""),
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMilliseconds(1) }
            }
        };

        var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Headers = { ETag = new EntityTagHeaderValue("\"abc123\"") }
        };

        innerHandler.SetResponses(initialResponse, notModifiedResponse);

        // Act
        var response1 = await httpClient.GetAsync("https://api.example.com/data");

        // Wait to ensure cache entry is stale
        await Task.Delay(10);

        var response2 = await httpClient.GetAsync("https://api.example.com/data");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode); // Handler converts 304 to 200 with cached content
        Assert.Equal(2, innerHandler.RequestCount);

        // Check that second request had conditional headers
        var secondRequest = innerHandler.Requests[1];
        Assert.Contains(secondRequest.Headers.IfNoneMatch, etag => etag.Tag == "\"abc123\"");
    }

    [Fact]
    public async Task Post_Request_NotCached_ByDefault()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Response"),
            Headers = { { "Cache-Control", "max-age=300" } }
        };

        _innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await _httpClient.PostAsync("https://api.example.com/data", new StringContent("test data"));
        var response2 = await _httpClient.PostAsync("https://api.example.com/data", new StringContent("test data"));

        // Assert
        Assert.Equal(2, _innerHandler.RequestCount); // Both requests went through
    }

    [Fact]
    public async Task Get_Request_BypassesCache_WhenNoCacheRequested()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Hello, World!"),
            Headers = { { "Cache-Control", "max-age=300" } }
        };

        _innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await _httpClient.SendAsync(request);
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data");
        request2.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var response2 = await _httpClient.SendAsync(request2);

        // Assert
        Assert.Equal(2, _innerHandler.RequestCount); // Cache bypassed
    }

    [Fact]
    public async Task Get_Request_DoesNotCache_WhenNoStoreResponseDirective()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Sensitive data"),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

        _innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        // Act
        var response1 = await _httpClient.GetAsync("https://api.example.com/data");
        var response2 = await _httpClient.GetAsync("https://api.example.com/data");

        // Assert
        Assert.Equal(2, _innerHandler.RequestCount); // Not cached due to no-store
    }
}

/// <summary>
/// Test helper for simulating HTTP responses.
/// </summary>
public class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpRequestMessage> _requests = new();

    public int RequestCount => _requests.Count;
    public IReadOnlyList<HttpRequestMessage> Requests => _requests;
    public bool ShouldThrowException { get; set; } = false;

    public void SetResponse(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
    }

    public void SetResponses(params HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
        {
            _responses.Enqueue(response);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _requests.Add(CloneRequest(request));

        if (ShouldThrowException)
        {
            throw new HttpRequestException("Simulated network error");
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No response configured");
        }

        var response = _responses.Dequeue();
        return Task.FromResult(CloneResponse(response));
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content != null)
        {
            // Simple content cloning for tests
            clone.Content = new StringContent("");
        }

        return clone;
    }

    public static HttpResponseMessage CloneResponse(HttpResponseMessage original)
    {
        var clone = new HttpResponseMessage(original.StatusCode);

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (original.Content != null)
        {
            var contentBytes = original.Content.ReadAsByteArrayAsync().Result;
            clone.Content = new ByteArrayContent(contentBytes);

            foreach (var header in original.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}