using System.Net.Http.Headers;
using MethodCache.HttpCaching.Validation;
using Xunit;

namespace MethodCache.HttpCaching.UnitTests;

public class VaryHeaderCacheKeyGeneratorTests
{
    private readonly VaryHeaderCacheKeyGenerator _generator;

    public VaryHeaderCacheKeyGeneratorTests()
    {
        _generator = new VaryHeaderCacheKeyGenerator();
    }

    [Fact]
    public void GenerateKey_WithoutVaryHeaders_ReturnsBasicKey()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        var varyHeaders = Array.Empty<string>();

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        Assert.Equal("GET:https://api.example.com/users", key);
    }

    [Fact]
    public void GenerateKey_WithSingleVaryHeader_IncludesHeaderValue()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var varyHeaders = new[] { "Accept" };

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        Assert.Equal("GET:https://api.example.com/users:Accept=application/json", key);
    }

    [Fact]
    public void GenerateKey_WithMultipleVaryHeaders_IncludesAllHeaderValues()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var varyHeaders = new[] { "Accept", "Accept-Language" };

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        Assert.Equal("GET:https://api.example.com/users:Accept=application/json:Accept-Language=en-US", key);
    }

    [Fact]
    public void GenerateKey_WithMissingVaryHeader_UsesEmptyValue()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var varyHeaders = new[] { "Accept", "User-Agent" }; // User-Agent not present

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        Assert.Equal("GET:https://api.example.com/users:Accept=application/json:User-Agent=", key);
    }

    [Fact]
    public void GenerateKey_WithCaseInsensitiveHeaders_NormalizesCase()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var varyHeaders = new[] { "accept", "ACCEPT-LANGUAGE" }; // Different cases

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        Assert.Equal("GET:https://api.example.com/users:Accept=application/json:Accept-Language=", key);
    }

    [Fact]
    public void GenerateKey_WithMultipleHeaderValues_ConcatenatesWithComma()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var varyHeaders = new[] { "Accept" };

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        Assert.Equal("GET:https://api.example.com/users:Accept=application/json, application/xml", key);
    }

    [Fact]
    public void GenerateKey_WithAuthorizationHeader_IncludesHashedValue()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        var varyHeaders = new[] { "Authorization" };

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        // Should include a hashed version of the auth header for privacy
        Assert.StartsWith("GET:https://api.example.com/users:Authorization=", key);
        Assert.DoesNotContain("secret-token", key);
    }

    [Fact]
    public void GenerateKey_WithVaryAsterisk_ReturnsUncacheableKey()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        var varyHeaders = new[] { "*" };

        // Act
        var key = _generator.GenerateKey(request, varyHeaders);

        // Assert
        Assert.Equal("UNCACHEABLE:*", key);
    }
}