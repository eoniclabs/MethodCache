using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Validation;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class ContentNegotiationHandlerTests
{
    [Fact]
    public void ParseAcceptHeader_PopulatesMediaTypes()
    {
        var options = new CacheVariationOptions();
        var handler = new ContentNegotiationHandler(options);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.Accept.ParseAdd("application/json");

        var accepted = handler.ParseAcceptHeader(request);

        Assert.Single(accepted.ContentTypes);
        Assert.Equal("application/json", accepted.ContentTypes[0].MediaType);
    }

    [Fact]
    public void IsAcceptable_MatchingContentType_ReturnsTrue()
    {
        var options = new CacheVariationOptions();
        var handler = new ContentNegotiationHandler(options);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.Accept.ParseAdd("application/json");

        var accepted = handler.ParseAcceptHeader(request);

        var entry = new HttpCacheEntry
        {
            RequestUri = "https://example.com",
            Method = HttpMethod.Get.Method,
            StatusCode = System.Net.HttpStatusCode.OK,
            Content = new byte[] { 1, 2, 3 },
            Headers = new Dictionary<string, string[]>(),
            ContentHeaders = new Dictionary<string, string[]>
            {
                ["Content-Type"] = new[] { "application/json" }
            }
        };

        Assert.True(handler.IsAcceptable(entry, accepted));
    }
}

