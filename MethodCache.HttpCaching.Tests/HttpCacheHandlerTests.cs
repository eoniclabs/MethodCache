using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpCacheHandlerTests
{
    private readonly HttpClient _httpClient;
    private readonly TestHttpMessageHandler _innerHandler;

    public HttpCacheHandlerTests()
    {
        _innerHandler = new TestHttpMessageHandler();
        var handler = HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), _innerHandler);
        _httpClient = new HttpClient(handler);
    }

    [Fact]
    public async Task Get_Request_CachesResponse_WhenResponseHasCacheControl()
    {
        var expectedResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Hello, World!"),
            Headers = { { "Cache-Control", "max-age=300" } }
        };

        _innerHandler.SetResponses(expectedResponse, expectedResponse);

        var response1 = await _httpClient.GetAsync("https://api.example.com/data");
        var response2 = await _httpClient.GetAsync("https://api.example.com/data");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(1, _innerHandler.RequestCount);
    }

    [Fact]
    public async Task Get_Request_Returns304_WhenETagMatches()
    {
        var options = new HttpCacheOptions();
        options.Freshness.DefaultMaxAge = TimeSpan.FromMilliseconds(1);

        var innerHandler = new TestHttpMessageHandler();
        var handler = HttpCacheTestFactory.CreateHandler(options, innerHandler);
        var httpClient = new HttpClient(handler);

        var initialResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Hello, World!"),
            Headers =
            {
                Date = DateTimeOffset.UtcNow,
                ETag = new EntityTagHeaderValue("\"abc123\""),
                CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMilliseconds(1) }
            }
        };

        var notModifiedResponse = new HttpResponseMessage(HttpStatusCode.NotModified)
        {
            Headers = {
                Date = DateTimeOffset.UtcNow,
                ETag = new EntityTagHeaderValue("\"abc123\"")
            }
        };

        innerHandler.SetResponses(initialResponse, notModifiedResponse);

        var response1 = await httpClient.GetAsync("https://api.example.com/data");

        await Task.Delay(10);

        var response2 = await httpClient.GetAsync("https://api.example.com/data");

        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        Assert.Equal(2, innerHandler.RequestCount);

        var secondRequest = innerHandler.Requests[1];
        Assert.Contains(secondRequest.Headers.IfNoneMatch, etag => etag.Tag == "\"abc123\"");
    }

    [Fact]
    public async Task Post_Request_NotCached_ByDefault()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Response"),
            Headers = { { "Cache-Control", "max-age=300" } }
        };

        _innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await _httpClient.PostAsync("https://api.example.com/data", new StringContent("test data"));
        var response2 = await _httpClient.PostAsync("https://api.example.com/data", new StringContent("test data"));

        Assert.Equal(2, _innerHandler.RequestCount);
    }

    [Fact]
    public async Task Get_Request_BypassesCache_WhenNoCacheRequested()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data")
        {
            Headers = { CacheControl = new CacheControlHeaderValue { NoCache = true } }
        };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Hello, World!"),
            Headers = { { "Cache-Control", "max-age=300" } }
        };

        _innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await _httpClient.SendAsync(request);
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data")
        {
            Headers = { CacheControl = new CacheControlHeaderValue { NoCache = true } }
        };
        var response2 = await _httpClient.SendAsync(request2);

        Assert.Equal(2, _innerHandler.RequestCount);
    }

    [Fact]
    public async Task Get_Request_DoesNotCache_WhenNoStoreResponseDirective()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Sensitive data"),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

        _innerHandler.SetResponses(response, TestHttpMessageHandler.CloneResponse(response));

        var response1 = await _httpClient.GetAsync("https://api.example.com/data");
        var response2 = await _httpClient.GetAsync("https://api.example.com/data");

        Assert.Equal(2, _innerHandler.RequestCount);
    }
}

public class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly List<HttpRequestMessage> _requests = new();

    public int RequestCount => _requests.Count;
    public IReadOnlyList<HttpRequestMessage> Requests => _requests;
    public bool ShouldThrowException { get; set; }

    public void SetResponse(HttpResponseMessage response) => _responses.Enqueue(response);

    public void SetResponses(params HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
        {
            _responses.Enqueue(response);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

        // Copy all headers except the special ones we'll handle separately
        foreach (var header in original.Headers)
        {
            if (header.Key != "If-None-Match" && header.Key != "If-Match")
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Special handling for IfNoneMatch header collection
        foreach (var etag in original.Headers.IfNoneMatch)
        {
            clone.Headers.IfNoneMatch.Add(etag);
        }

        // Special handling for IfMatch header collection
        foreach (var etag in original.Headers.IfMatch)
        {
            clone.Headers.IfMatch.Add(etag);
        }

        if (original.Content != null)
        {
            clone.Content = new StringContent(string.Empty);
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




