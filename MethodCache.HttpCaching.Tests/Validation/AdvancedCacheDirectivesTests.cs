using MethodCache.HttpCaching.Validation;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class AdvancedCacheDirectivesTests
{
    [Fact]
    public void ParseResponse_WithMustRevalidate_ShouldSetFlag()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue { MustRevalidate = true };
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.True(directives.MustRevalidate);
    }

    [Fact]
    public void ParseResponse_WithProxyRevalidate_ShouldSetFlag()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.CacheControl = new CacheControlHeaderValue { ProxyRevalidate = true };
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.True(directives.ProxyRevalidate);
    }

    [Fact]
    public void ParseResponse_WithImmutableExtension_ShouldSetFlag()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var cacheControl = new CacheControlHeaderValue();
        cacheControl.Extensions.Add(new NameValueHeaderValue("immutable"));
        response.Headers.CacheControl = cacheControl;
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.True(directives.Immutable);
    }

    [Fact]
    public void ParseResponse_WithStaleWhileRevalidate_ShouldParseValue()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var cacheControl = new CacheControlHeaderValue();
        cacheControl.Extensions.Add(new NameValueHeaderValue("stale-while-revalidate", "300"));
        response.Headers.CacheControl = cacheControl;
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(300), directives.StaleWhileRevalidate);
    }

    [Fact]
    public void ParseResponse_WithStaleIfError_ShouldParseValue()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var cacheControl = new CacheControlHeaderValue();
        cacheControl.Extensions.Add(new NameValueHeaderValue("stale-if-error", "600"));
        response.Headers.CacheControl = cacheControl;
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(600), directives.StaleIfError);
    }

    [Fact]
    public void ParseResponse_WithMustUnderstandExtension_ShouldSetFlag()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var cacheControl = new CacheControlHeaderValue();
        cacheControl.Extensions.Add(new NameValueHeaderValue("must-understand"));
        response.Headers.CacheControl = cacheControl;
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.True(directives.MustUnderstand);
    }

    [Fact]
    public void ParseResponse_WithSurrogateControl_ShouldParseDirectives()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Surrogate-Control", "max-age=3600, no-store");
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(3600), directives.SurrogateMaxAge);
        Assert.True(directives.SurrogateNoStore);
    }

    [Fact]
    public void ParseResponse_WithNoStoreRemote_ShouldSetFlag()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Surrogate-Control", "no-store-remote");
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.True(directives.SurrogateNoStoreRemote);
    }

    [Fact]
    public void CanUseStaleResponse_WithMustRevalidate_ShouldReturnFalse()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { MustRevalidate = true };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.CanUseStaleResponse(responseDirectives, requestDirectives, false, false);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CanUseStaleResponse_WithProxyRevalidateInSharedCache_ShouldReturnFalse()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { ProxyRevalidate = true };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.CanUseStaleResponse(responseDirectives, requestDirectives, true, false);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void CanUseStaleResponse_WithProxyRevalidateInPrivateCache_ShouldAllowStale()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { ProxyRevalidate = true };
        var requestDirectives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.MaxStale))!
            .SetValue(requestDirectives, TimeSpan.FromMinutes(10));

        // Act
        var result = handler.CanUseStaleResponse(responseDirectives, requestDirectives, false, false);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanUseStaleResponse_WithStaleIfErrorDuringError_ShouldReturnTrue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives
        {
            StaleIfError = TimeSpan.FromMinutes(30)
        };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.CanUseStaleResponse(responseDirectives, requestDirectives, false, true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanUseStaleResponse_WithStaleWhileRevalidate_ShouldReturnTrue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives
        {
            StaleWhileRevalidate = TimeSpan.FromMinutes(10)
        };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.CanUseStaleResponse(responseDirectives, requestDirectives, false, false);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void RequiresRevalidation_WithNoCache_ShouldReturnTrue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { NoCache = true };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.RequiresRevalidation(responseDirectives, requestDirectives, false, false);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void RequiresRevalidation_WithFreshImmutable_ShouldReturnFalse()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { Immutable = true };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.RequiresRevalidation(responseDirectives, requestDirectives, false, false);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void RequiresRevalidation_WithStaleMustRevalidate_ShouldReturnTrue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { MustRevalidate = true };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.RequiresRevalidation(responseDirectives, requestDirectives, true, false);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void RequiresRevalidation_WithStaleProxyRevalidateInSharedCache_ShouldReturnTrue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { ProxyRevalidate = true };
        var requestDirectives = new RequestCacheDirectives();

        // Act
        var result = handler.RequiresRevalidation(responseDirectives, requestDirectives, true, true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsCacheable_WithNoStore_ShouldReturnFalse()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var directives = new AdvancedCacheDirectives.ResponseDirectives { NoStore = true };

        // Act
        var result = handler.IsCacheable(directives, false);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsCacheable_WithSurrogateNoStoreInSharedCache_ShouldReturnFalse()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var directives = new AdvancedCacheDirectives.ResponseDirectives { SurrogateNoStore = true };

        // Act
        var result = handler.IsCacheable(directives, true);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsCacheable_WithPrivateInSharedCache_ShouldReturnFalse()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var directives = new AdvancedCacheDirectives.ResponseDirectives { Private = true };

        // Act
        var result = handler.IsCacheable(directives, true);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsCacheable_WithPrivateInPrivateCache_ShouldReturnTrue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var directives = new AdvancedCacheDirectives.ResponseDirectives { Private = true };

        // Act
        var result = handler.IsCacheable(directives, false);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetEffectiveMaxAge_WithSurrogateMaxAgeInSharedCache_ShouldReturnSurrogateValue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var directives = new AdvancedCacheDirectives.ResponseDirectives
        {
            SurrogateMaxAge = TimeSpan.FromHours(2)
        };
        var standardMaxAge = TimeSpan.FromHours(1);

        // Act
        var result = handler.GetEffectiveMaxAge(directives, standardMaxAge, true);

        // Assert
        Assert.Equal(TimeSpan.FromHours(2), result);
    }

    [Fact]
    public void GetEffectiveMaxAge_WithSurrogateMaxAgeInPrivateCache_ShouldReturnStandardValue()
    {
        // Arrange
        var handler = new AdvancedCacheDirectives();
        var directives = new AdvancedCacheDirectives.ResponseDirectives
        {
            SurrogateMaxAge = TimeSpan.FromHours(2)
        };
        var standardMaxAge = TimeSpan.FromHours(1);

        // Act
        var result = handler.GetEffectiveMaxAge(directives, standardMaxAge, false);

        // Assert
        Assert.Equal(TimeSpan.FromHours(1), result);
    }

    [Fact]
    public void ParseResponse_WithNoCacheControl_ShouldReturnEmptyDirectives()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.False(directives.MustRevalidate);
        Assert.False(directives.ProxyRevalidate);
        Assert.False(directives.NoCache);
        Assert.False(directives.NoStore);
        Assert.False(directives.Private);
        Assert.False(directives.Public);
        Assert.False(directives.NoTransform);
        Assert.False(directives.Immutable);
        Assert.False(directives.MustUnderstand);
        Assert.Null(directives.StaleWhileRevalidate);
        Assert.Null(directives.StaleIfError);
    }

    [Fact]
    public void ParseResponse_WithQuotedExtensionValues_ShouldParseCorrectly()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var cacheControl = new CacheControlHeaderValue();
        cacheControl.Extensions.Add(new NameValueHeaderValue("stale-while-revalidate", "\"120\""));
        response.Headers.CacheControl = cacheControl;
        var handler = new AdvancedCacheDirectives();

        // Act
        var directives = handler.ParseResponse(response);

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(120), directives.StaleWhileRevalidate);
    }

    [Fact]
    public void Constructor_WithCustomOptions_ShouldUseProvidedOptions()
    {
        // Arrange
        var options = new AdvancedCacheDirectives.Options
        {
            RespectMustRevalidate = false,
            RespectProxyRevalidate = false
        };

        // Act
        var handler = new AdvancedCacheDirectives(options);
        var responseDirectives = new AdvancedCacheDirectives.ResponseDirectives { MustRevalidate = true };
        var requestDirectives = new RequestCacheDirectives();
        typeof(RequestCacheDirectives)
            .GetProperty(nameof(RequestCacheDirectives.MaxStale))!
            .SetValue(requestDirectives, TimeSpan.FromMinutes(10));

        // Assert - Should allow stale because must-revalidate is not respected
        var result = handler.CanUseStaleResponse(responseDirectives, requestDirectives, false, false);
        Assert.True(result);
    }
}