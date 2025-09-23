using MethodCache.HttpCaching;
using MethodCache.HttpCaching.Validation;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Validation;

public class ContentNegotiationHandlerTests
{
    [Fact]
    public void ParseAcceptHeader_WithMediaTypes_ShouldParseCorrectly()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html", 1.0));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.1));

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.ParseAcceptHeader(request);

        // Assert
        Assert.Equal(3, result.ContentTypes.Count);

        // Should be sorted by quality descending
        Assert.Equal("text/html", result.ContentTypes[0].MediaType);
        Assert.Equal(1.0, result.ContentTypes[0].Quality);

        Assert.Equal("application/json", result.ContentTypes[1].MediaType);
        Assert.Equal(0.9, result.ContentTypes[1].Quality);

        Assert.Equal("*/*", result.ContentTypes[2].MediaType);
        Assert.Equal(0.1, result.ContentTypes[2].Quality);
    }

    [Fact]
    public void ParseAcceptHeader_WithLanguages_ShouldParseCorrectly()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US", 1.0));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.8));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr", 0.6));

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.ParseAcceptHeader(request);

        // Assert
        Assert.Equal(3, result.Languages.Count);
        Assert.Equal("en-US", result.Languages[0].Language);
        Assert.Equal(1.0, result.Languages[0].Quality);
        Assert.Equal("en", result.Languages[1].Language);
        Assert.Equal(0.8, result.Languages[1].Quality);
    }

    [Fact]
    public void ParseAcceptHeader_WithEncodings_ShouldParseAndAddIdentity()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip", 1.0));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate", 0.8));

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.ParseAcceptHeader(request);

        // Assert
        Assert.Equal(3, result.Encodings.Count); // gzip, deflate, and auto-added identity
        Assert.Contains(result.Encodings, e => e.Encoding == "gzip" && e.Quality == 1.0);
        Assert.Contains(result.Encodings, e => e.Encoding == "deflate" && e.Quality == 0.8);
        Assert.Contains(result.Encodings, e => e.Encoding == "identity" && e.Quality == 1.0);
    }

    [Fact]
    public void ParseAcceptHeader_WithCharsets_ShouldParseCorrectly()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8", 1.0));
        request.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("iso-8859-1", 0.5));

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.ParseAcceptHeader(request);

        // Assert
        Assert.Equal(2, result.Charsets.Count);
        Assert.Equal("utf-8", result.Charsets[0].Charset);
        Assert.Equal(1.0, result.Charsets[0].Quality);
    }

    [Fact]
    public void ParseAcceptHeader_WithDisabledFeatures_ShouldSkipParsing()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.AcceptCharset.Add(new StringWithQualityHeaderValue("utf-8"));

        var options = new ContentNegotiationHandler.Options
        {
            EnableAcceptLanguage = false,
            EnableAcceptEncoding = false,
            EnableAcceptCharset = false
        };
        var handler = new ContentNegotiationHandler(options);

        // Act
        var result = handler.ParseAcceptHeader(request);

        // Assert
        Assert.Empty(result.Languages);
        Assert.Empty(result.Encodings);
        Assert.Empty(result.Charsets);
    }

    [Fact]
    public void IsAcceptable_WithMatchingContentType_ShouldReturnTrue()
    {
        // Arrange
        var cacheEntry = CreateCacheEntry("application/json");
        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "application/json", Quality = 1.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.IsAcceptable(cacheEntry, acceptedContent);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsAcceptable_WithWildcardContentType_ShouldReturnTrue()
    {
        // Arrange
        var cacheEntry = CreateCacheEntry("application/json");
        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "*/*", Quality = 1.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.IsAcceptable(cacheEntry, acceptedContent);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsAcceptable_WithSubtypeWildcard_ShouldReturnTrue()
    {
        // Arrange
        var cacheEntry = CreateCacheEntry("application/json");
        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "application/*", Quality = 1.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.IsAcceptable(cacheEntry, acceptedContent);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsAcceptable_WithNonMatchingContentType_ShouldReturnFalse()
    {
        // Arrange
        var cacheEntry = CreateCacheEntry("application/json");
        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "text/html", Quality = 1.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.IsAcceptable(cacheEntry, acceptedContent);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsAcceptable_WithZeroQuality_ShouldReturnFalse()
    {
        // Arrange
        var cacheEntry = CreateCacheEntry("application/json");
        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "application/json", Quality = 0.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.IsAcceptable(cacheEntry, acceptedContent);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GenerateVariantKey_ShouldCreateUniqueKeys()
    {
        // Arrange
        var request = new HttpRequestMessage();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.Add("User-Agent", "TestAgent/1.0");

        var varyHeaders = new[] { "Accept", "Accept-Language", "Accept-Encoding", "User-Agent" };
        var handler = new ContentNegotiationHandler();

        // Act
        var key = handler.GenerateVariantKey(request, varyHeaders);

        // Assert
        Assert.Contains("accept:", key);
        Assert.Contains("lang:", key);
        Assert.Contains("enc:", key);
        Assert.Contains("user-agent:", key);
    }

    [Fact]
    public void SelectBestVariant_ShouldChooseHighestScoringVariant()
    {
        // Arrange
        var variants = new[]
        {
            CreateCacheEntry("application/json"),
            CreateCacheEntry("text/html"),
            CreateCacheEntry("application/xml")
        };

        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "application/json", Quality = 1.0 },
                new() { MediaType = "text/html", Quality = 0.9 },
                new() { MediaType = "application/xml", Quality = 0.8 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var best = handler.SelectBestVariant(variants, acceptedContent);

        // Assert
        Assert.NotNull(best);
        using var response = best.ToHttpResponse();
        Assert.Equal("application/json", response.Content?.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void SelectBestVariant_WithNoAcceptableVariants_ShouldReturnNull()
    {
        // Arrange
        var variants = new[]
        {
            CreateCacheEntry("application/json")
        };

        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "text/html", Quality = 1.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var best = handler.SelectBestVariant(variants, acceptedContent);

        // Assert
        Assert.Null(best);
    }

    [Fact]
    public void ParseAcceptHeader_WithNoHeaders_ShouldReturnEmpty()
    {
        // Arrange
        var request = new HttpRequestMessage();
        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.ParseAcceptHeader(request);

        // Assert
        Assert.Empty(result.ContentTypes);
        Assert.Empty(result.Languages);
        Assert.Empty(result.Charsets);
        // Encodings should be empty when no Accept-Encoding header is present
        Assert.Empty(result.Encodings);
    }

    [Fact]
    public void IsAcceptable_WithMatchingLanguage_ShouldReturnTrue()
    {
        // Arrange
        var cacheEntry = CreateCacheEntryWithLanguage("application/json", "en-US");
        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "application/json", Quality = 1.0 }
            },
            Languages = new List<LanguageWithQuality>
            {
                new() { Language = "en", Quality = 1.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.IsAcceptable(cacheEntry, acceptedContent);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsAcceptable_WithNonMatchingLanguage_ShouldReturnFalse()
    {
        // Arrange
        var cacheEntry = CreateCacheEntryWithLanguage("application/json", "fr-FR");
        var acceptedContent = new AcceptedContent
        {
            ContentTypes = new List<MediaTypeWithQuality>
            {
                new() { MediaType = "application/json", Quality = 1.0 }
            },
            Languages = new List<LanguageWithQuality>
            {
                new() { Language = "en", Quality = 1.0 }
            }
        };

        var handler = new ContentNegotiationHandler();

        // Act
        var result = handler.IsAcceptable(cacheEntry, acceptedContent);

        // Assert
        Assert.False(result);
    }

    private HttpCacheEntry CreateCacheEntry(string contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("test", System.Text.Encoding.UTF8, contentType);

        return new HttpCacheEntry
        {
            RequestUri = "https://example.com/test",
            Method = "GET",
            StatusCode = response.StatusCode,
            Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
            ContentHeaders = response.Content.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
            Content = System.Text.Encoding.UTF8.GetBytes("test"),
            StoredAt = DateTimeOffset.Now
        };
    }

    private HttpCacheEntry CreateCacheEntryWithLanguage(string contentType, string language)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent("test", System.Text.Encoding.UTF8, contentType);
        response.Content.Headers.ContentLanguage.Add(language);

        return new HttpCacheEntry
        {
            RequestUri = "https://example.com/test",
            Method = "GET",
            StatusCode = response.StatusCode,
            Headers = response.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
            ContentHeaders = response.Content.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray()),
            Content = System.Text.Encoding.UTF8.GetBytes("test"),
            StoredAt = DateTimeOffset.Now
        };
    }
}