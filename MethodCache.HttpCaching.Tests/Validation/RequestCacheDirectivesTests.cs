using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class RequestCacheDirectivesTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    [Fact]
    public void Parse_WithMaxAgeAndMinFresh_SetsProperties()
    {
        var directives = Parse(new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(30), MinFresh = TimeSpan.FromSeconds(5) });

        Assert.Equal(TimeSpan.FromSeconds(30), directives.MaxAge);
        Assert.Equal(TimeSpan.FromSeconds(5), directives.MinFresh);
    }

    [Fact]
    public void Accepts_FreshResponse_WithoutDirectives()
        => Assert.True(Parse(null).Accepts(TimeSpan.FromSeconds(10), Lifetime, staleAllowed: true));

    [Fact]
    public void Accepts_RejectsStaleResponse_WithoutMaxStale()
        => Assert.False(Parse(null).Accepts(TimeSpan.FromSeconds(70), Lifetime, staleAllowed: true));

    [Fact]
    public void Accepts_RejectsResponseOlderThanRequestMaxAge()
        => Assert.False(Parse(new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(5) }).Accepts(TimeSpan.FromSeconds(10), Lifetime, staleAllowed: true));

    [Fact]
    public void Accepts_RejectsResponseThatIsNotFreshForMinFresh()
        => Assert.False(Parse(new CacheControlHeaderValue { MinFresh = TimeSpan.FromSeconds(30) }).Accepts(TimeSpan.FromSeconds(40), Lifetime, staleAllowed: true));

    [Fact]
    public void Accepts_StaleResponseWithinMaxStale()
        => Assert.True(Parse(new CacheControlHeaderValue { MaxStale = true, MaxStaleLimit = TimeSpan.FromSeconds(30) }).Accepts(TimeSpan.FromSeconds(80), Lifetime, staleAllowed: true));

    [Fact]
    public void Accepts_RejectsMaxStale_WhenResponseMustRevalidate()
        => Assert.False(Parse(new CacheControlHeaderValue { MaxStale = true }).Accepts(TimeSpan.FromSeconds(80), Lifetime, staleAllowed: false));

    [Fact]
    public void Parse_OnlyIfCached()
        => Assert.True(Parse(new CacheControlHeaderValue { OnlyIfCached = true }).OnlyIfCached);

    private static RequestCacheDirectives Parse(CacheControlHeaderValue? cacheControl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        request.Headers.CacheControl = cacheControl;
        return RequestCacheDirectives.Parse(request);
    }
}
