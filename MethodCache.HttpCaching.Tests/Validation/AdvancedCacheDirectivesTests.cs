using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Validation;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class AdvancedCacheDirectivesTests
{
    [Fact]
    public void IsCacheable_PrivateResponse_FalseForSharedCache()
    {
        var behavior = new CacheBehaviorOptions { IsSharedCache = true };
        var directives = new AdvancedCacheDirectives(behavior);

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { Private = true } }
        };

        var parsed = directives.ParseResponse(response);
        Assert.False(directives.IsCacheable(parsed, behavior.IsSharedCache));
    }

    [Fact]
    public void IsCacheable_PublicResponse_TrueForSharedCache()
    {
        var behavior = new CacheBehaviorOptions { IsSharedCache = true };
        var directives = new AdvancedCacheDirectives(behavior);

        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromMinutes(5) } }
        };

        var parsed = directives.ParseResponse(response);
        Assert.True(directives.IsCacheable(parsed, behavior.IsSharedCache));
    }

    [Fact]
    public void CanUseStaleResponse_WhenStaleIfErrorEnabled_ReturnsTrue()
    {
        var behavior = new CacheBehaviorOptions { EnableStaleIfError = true };
        var directives = new AdvancedCacheDirectives(behavior);

        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives
        {
            StaleIfError = TimeSpan.FromMinutes(5)
        };

        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.CacheControl = new CacheControlHeaderValue { OnlyIfCached = true };
        var requestDirectives = RequestCacheDirectives.Parse(request);

        Assert.True(directives.CanUseStaleResponse(responseDirectives, requestDirectives, isSharedCache: false, isErrorCondition: true));
    }
}
