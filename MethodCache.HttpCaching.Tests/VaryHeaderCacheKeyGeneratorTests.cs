using System.Net.Http.Headers;
using MethodCache.HttpCaching.Validation;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

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

        // Assert - Keys are now hashed for compactness
        Assert.NotNull(key);
        Assert.NotEmpty(key);

        // Verify deterministic - same input produces same output
        var key2 = _generator.GenerateKey(request, varyHeaders);
        Assert.Equal(key, key2);
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

        // Assert - Verify key changes when header value changes
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var key2 = _generator.GenerateKey(request2, varyHeaders);

        Assert.NotEqual(key, key2); // Different header values produce different keys
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

        // Assert - Verify changing any header changes the key
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request2.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr-FR")); // Different language
        var key2 = _generator.GenerateKey(request2, varyHeaders);

        Assert.NotEqual(key, key2);
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

        // Assert - Verify missing header produces different key than populated header
        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request2.Headers.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("TestAgent", "1.0"));
        var key2 = _generator.GenerateKey(request2, varyHeaders);

        Assert.NotEqual(key, key2);
    }

    [Fact]
    public void GenerateKey_WithCaseInsensitiveHeaders_NormalizesCase()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var varyHeaders1 = new[] { "accept" }; // lowercase
        var varyHeaders2 = new[] { "Accept" }; // proper case
        var varyHeaders3 = new[] { "ACCEPT" }; // uppercase

        // Act
        var key1 = _generator.GenerateKey(request, varyHeaders1);
        var key2 = _generator.GenerateKey(request, varyHeaders2);
        var key3 = _generator.GenerateKey(request, varyHeaders3);

        // Assert - All variations should produce the same key (case normalization)
        Assert.Equal(key1, key2);
        Assert.Equal(key2, key3);
    }

    [Fact]
    public void GenerateKey_WithMultipleHeaderValues_ConcatenatesWithComma()
    {
        // Arrange
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var varyHeaders = new[] { "Accept" };

        // Act
        var key1 = _generator.GenerateKey(request1, varyHeaders);
        var key2 = _generator.GenerateKey(request2, varyHeaders);

        // Assert - Multiple header values should produce different key than single value
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void GenerateKey_WithAuthorizationHeader_IncludesHashedValue()
    {
        // Arrange
        var request1 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");

        var request2 = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/users");
        request2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "different-token");

        var varyHeaders = new[] { "Authorization" };

        // Act
        var key1 = _generator.GenerateKey(request1, varyHeaders);
        var key2 = _generator.GenerateKey(request2, varyHeaders);

        // Assert - Different auth tokens should produce different keys
        Assert.NotEqual(key1, key2);
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