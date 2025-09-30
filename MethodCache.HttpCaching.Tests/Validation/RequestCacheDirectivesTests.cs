using System.Net.Http;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Validation;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class RequestCacheDirectivesTests
{
    [Fact]
    public void Parse_WithMaxAgeAndMinFresh_SetsProperties()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromSeconds(30),
            MinFresh = TimeSpan.FromSeconds(5)
        };

        var directives = RequestCacheDirectives.Parse(request);

        Assert.Equal(TimeSpan.FromSeconds(30), directives.MaxAge);
        Assert.Equal(TimeSpan.FromSeconds(5), directives.MinFresh);
    }

    [Fact]
    public void RequiresCacheOnly_RespectsBehaviorSetting()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
        {
            OnlyIfCached = true
        };

        var behavior = new CacheBehaviorOptions { RespectOnlyIfCached = true };
        var directives = RequestCacheDirectives.Parse(request);

        Assert.True(directives.RequiresCacheOnly(behavior));
    }
}
