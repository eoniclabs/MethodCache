using MethodCache.HttpCaching.Validation;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class RequestCacheDirectivesTests
{
    [Fact]
    public void Parse_WithNoCache_ShouldSetNoCacheFlag()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.True(directives.NoCache);
    }

    [Fact]
    public void Parse_WithMaxAge_ShouldSetMaxAgeValue()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxAge = TimeSpan.FromMinutes(30)
        };

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(30), directives.MaxAge);
    }

    [Fact]
    public void Parse_WithMaxStaleNoValue_ShouldSetMaxStaleToMaxValue()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxStale = true
        };

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.Equal(TimeSpan.MaxValue, directives.MaxStale);
    }

    [Fact]
    public void Parse_WithMaxStaleValue_ShouldSetSpecificValue()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            MaxStaleLimit = TimeSpan.FromMinutes(15)
        };

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(15), directives.MaxStale);
    }

    [Fact]
    public void Parse_WithMinFresh_ShouldSetMinFreshValue()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            MinFresh = TimeSpan.FromMinutes(10)
        };

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(10), directives.MinFresh);
    }

    [Fact]
    public void Parse_WithOnlyIfCached_ShouldSetFlag()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            OnlyIfCached = true
        };

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.True(directives.OnlyIfCached);
    }

    [Fact]
    public void Parse_WithMustUnderstandExtension_ShouldSetFlag()
    {
        // Arrange
        var request = new HttpRequestMessage();
        var cacheControl = new CacheControlHeaderValue();
        cacheControl.Extensions.Add(new NameValueHeaderValue("must-understand"));
        request.Headers.CacheControl = cacheControl;

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.True(directives.MustUnderstand);
    }

    [Fact]
    public void IsSatisfiedBy_WithMaxAge_ShouldRespectConstraint()
    {
        // Arrange
        var directives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.MaxAge))!
            .SetValue(directives, TimeSpan.FromMinutes(30));

        var entry = CreateMockCacheEntry();
        var currentAge = TimeSpan.FromMinutes(45); // Older than max-age
        var freshnessLifetime = TimeSpan.FromHours(1);

        // Act
        var result = directives.IsSatisfiedBy(entry, currentAge, freshnessLifetime);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsSatisfiedBy_WithMinFresh_ShouldRespectConstraint()
    {
        // Arrange
        var directives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.MinFresh))!
            .SetValue(directives, TimeSpan.FromMinutes(20));

        var entry = CreateMockCacheEntry();
        var currentAge = TimeSpan.FromMinutes(50); // Only 10 minutes of freshness left
        var freshnessLifetime = TimeSpan.FromHours(1);

        // Act
        var result = directives.IsSatisfiedBy(entry, currentAge, freshnessLifetime);

        // Assert
        Assert.False(result); // 10 minutes remaining < 20 minutes required
    }

    [Fact]
    public void IsSatisfiedBy_WithMaxStale_ShouldAllowStaleContent()
    {
        // Arrange
        var directives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.MaxStale))!
            .SetValue(directives, TimeSpan.FromMinutes(30));

        var entry = CreateMockCacheEntry();
        var currentAge = TimeSpan.FromMinutes(90); // 30 minutes stale
        var freshnessLifetime = TimeSpan.FromHours(1);

        // Act
        var result = directives.IsSatisfiedBy(entry, currentAge, freshnessLifetime);

        // Assert
        Assert.True(result); // Within max-stale limit
    }

    [Fact]
    public void IsSatisfiedBy_WithNoCache_ShouldAlwaysBeFalse()
    {
        // Arrange
        var directives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.NoCache))!
            .SetValue(directives, true);

        var entry = CreateMockCacheEntry();
        var currentAge = TimeSpan.FromMinutes(5);
        var freshnessLifetime = TimeSpan.FromHours(1);

        // Act
        var result = directives.IsSatisfiedBy(entry, currentAge, freshnessLifetime);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void RequiresCacheOnly_ShouldReturnOnlyIfCachedValue()
    {
        // Arrange
        var directives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.OnlyIfCached))!
            .SetValue(directives, true);

        // Act
        var result = directives.RequiresCacheOnly();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void AllowsStale_ShouldReturnTrueWhenMaxStaleSet()
    {
        // Arrange
        var directives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.MaxStale))!
            .SetValue(directives, TimeSpan.FromMinutes(30));

        // Act
        var result = directives.AllowsStale();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void BypassCache_ShouldCreateDirectivesWithNoCacheAndNoStore()
    {
        // Act
        var directives = RequestCacheDirectives.BypassCache();

        // Assert
        Assert.True(directives.NoCache);
        Assert.True(directives.NoStore);
    }

    [Fact]
    public void CacheOnly_ShouldCreateDirectivesWithOnlyIfCached()
    {
        // Act
        var directives = RequestCacheDirectives.CacheOnly();

        // Assert
        Assert.True(directives.OnlyIfCached);
    }

    [Fact]
    public void Parse_WithNoCacheControl_ShouldReturnEmptyDirectives()
    {
        // Arrange
        var request = new HttpRequestMessage();
        // No cache control header set

        // Act
        var directives = RequestCacheDirectives.Parse(request);

        // Assert
        Assert.False(directives.NoCache);
        Assert.False(directives.NoStore);
        Assert.Null(directives.MaxAge);
        Assert.Null(directives.MaxStale);
        Assert.Null(directives.MinFresh);
        Assert.False(directives.OnlyIfCached);
        Assert.False(directives.MustUnderstand);
    }

    private static HttpCacheEntry CreateMockCacheEntry()
    {
        return new HttpCacheEntry
        {
            RequestUri = "https://example.com/test",
            Method = "GET"
        };
    }
}