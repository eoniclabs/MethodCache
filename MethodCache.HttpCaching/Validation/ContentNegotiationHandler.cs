using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace MethodCache.HttpCaching.Validation;

/// <summary>
/// Handles HTTP content negotiation including Accept headers with quality values,
/// language preferences, and encoding negotiation according to RFC 9110.
/// </summary>
public class ContentNegotiationHandler
{
    /// <summary>
    /// Configuration options for content negotiation.
    /// </summary>
    public class Options
    {
        /// <summary>
        /// Gets or sets whether to enable quality value parsing (q values).
        /// </summary>
        public bool EnableQualityValues { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable Accept-Language negotiation.
        /// </summary>
        public bool EnableAcceptLanguage { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable Accept-Encoding negotiation.
        /// </summary>
        public bool EnableAcceptEncoding { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable Accept-Charset negotiation.
        /// </summary>
        public bool EnableAcceptCharset { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to store multiple variants per URL.
        /// </summary>
        public bool EnableMultipleVariants { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum number of variants to cache per URL.
        /// </summary>
        public int MaxVariantsPerUrl { get; set; } = 10;
    }

    private readonly Options _options;
    private static readonly Regex QualityValueRegex = new(@";q=([0-9\.]+)", RegexOptions.Compiled);

    public ContentNegotiationHandler(Options? options = null)
    {
        _options = options ?? new Options();
    }

    /// <summary>
    /// Parses Accept headers and returns a list of acceptable content types with their quality values.
    /// </summary>
    public AcceptedContent ParseAcceptHeader(HttpRequestMessage request)
    {
        var result = new AcceptedContent();

        // Parse Accept header for content types
        if (request.Headers.Accept?.Any() == true)
        {
            result.ContentTypes = ParseMediaTypes(request.Headers.Accept);
        }

        // Parse Accept-Language header
        if (_options.EnableAcceptLanguage && request.Headers.AcceptLanguage?.Any() == true)
        {
            result.Languages = ParseLanguages(request.Headers.AcceptLanguage);
        }

        // Parse Accept-Encoding header
        if (_options.EnableAcceptEncoding && request.Headers.AcceptEncoding?.Any() == true)
        {
            result.Encodings = ParseEncodings(request.Headers.AcceptEncoding);
        }

        // Parse Accept-Charset header
        if (_options.EnableAcceptCharset && request.Headers.AcceptCharset?.Any() == true)
        {
            result.Charsets = ParseCharsets(request.Headers.AcceptCharset);
        }

        return result;
    }

    private List<MediaTypeWithQuality> ParseMediaTypes(IEnumerable<MediaTypeWithQualityHeaderValue> acceptHeaders)
    {
        var types = new List<MediaTypeWithQuality>();

        foreach (var header in acceptHeaders)
        {
            types.Add(new MediaTypeWithQuality
            {
                MediaType = header.MediaType ?? "*/*",
                Quality = header.Quality ?? 1.0,
                Parameters = header.Parameters?.ToDictionary(p => p.Name, p => p.Value) ?? new Dictionary<string, string?>()
            });
        }

        // Sort by quality descending
        return types.OrderByDescending(t => t.Quality).ToList();
    }

    private List<LanguageWithQuality> ParseLanguages(IEnumerable<StringWithQualityHeaderValue> acceptLanguages)
    {
        var languages = new List<LanguageWithQuality>();

        foreach (var header in acceptLanguages)
        {
            languages.Add(new LanguageWithQuality
            {
                Language = header.Value,
                Quality = header.Quality ?? 1.0
            });
        }

        return languages.OrderByDescending(l => l.Quality).ToList();
    }

    private List<EncodingWithQuality> ParseEncodings(IEnumerable<StringWithQualityHeaderValue> acceptEncodings)
    {
        var encodings = new List<EncodingWithQuality>();

        foreach (var header in acceptEncodings)
        {
            encodings.Add(new EncodingWithQuality
            {
                Encoding = header.Value,
                Quality = header.Quality ?? 1.0
            });
        }

        // Special handling for identity encoding
        if (!encodings.Any(e => e.Encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            encodings.Add(new EncodingWithQuality { Encoding = "identity", Quality = 1.0 });
        }

        return encodings.OrderByDescending(e => e.Quality).ToList();
    }

    private List<CharsetWithQuality> ParseCharsets(IEnumerable<StringWithQualityHeaderValue> acceptCharsets)
    {
        var charsets = new List<CharsetWithQuality>();

        foreach (var header in acceptCharsets)
        {
            charsets.Add(new CharsetWithQuality
            {
                Charset = header.Value,
                Quality = header.Quality ?? 1.0
            });
        }

        return charsets.OrderByDescending(c => c.Quality).ToList();
    }

    /// <summary>
    /// Determines if a cached response matches the accept criteria.
    /// </summary>
    public bool IsAcceptable(HttpCacheEntry cacheEntry, AcceptedContent acceptedContent)
    {
        using var cachedResponse = cacheEntry.ToHttpResponse();

        // Check content type
        if (!IsContentTypeAcceptable(cachedResponse.Content?.Headers.ContentType, acceptedContent.ContentTypes))
            return false;

        // Check language
        if (_options.EnableAcceptLanguage && !IsLanguageAcceptable(cachedResponse.Content?.Headers.ContentLanguage, acceptedContent.Languages))
            return false;

        // Check encoding
        if (_options.EnableAcceptEncoding && !IsEncodingAcceptable(cachedResponse.Content?.Headers.ContentEncoding, acceptedContent.Encodings))
            return false;

        return true;
    }

    private bool IsContentTypeAcceptable(MediaTypeHeaderValue? responseContentType, List<MediaTypeWithQuality> acceptedTypes)
    {
        if (responseContentType == null || acceptedTypes.Count == 0)
            return true;

        var responseType = responseContentType.MediaType;

        foreach (var accepted in acceptedTypes.Where(a => a.Quality > 0))
        {
            if (accepted.MediaType == "*/*")
                return true;

            if (accepted.MediaType.EndsWith("/*"))
            {
                var prefix = accepted.MediaType.Substring(0, accepted.MediaType.Length - 2);
                if (responseType?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                    return true;
            }

            if (string.Equals(accepted.MediaType, responseType, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool IsLanguageAcceptable(ICollection<string>? responseLanguages, List<LanguageWithQuality> acceptedLanguages)
    {
        if (responseLanguages == null || responseLanguages.Count == 0 || acceptedLanguages.Count == 0)
            return true;

        foreach (var lang in responseLanguages)
        {
            foreach (var accepted in acceptedLanguages.Where(a => a.Quality > 0))
            {
                if (accepted.Language == "*")
                    return true;

                if (lang.StartsWith(accepted.Language, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private bool IsEncodingAcceptable(ICollection<string>? responseEncodings, List<EncodingWithQuality> acceptedEncodings)
    {
        if (acceptedEncodings.Count == 0)
            return true;

        // If no encoding, treat as identity
        if (responseEncodings == null || responseEncodings.Count == 0)
        {
            return acceptedEncodings.Any(e => e.Encoding == "identity" && e.Quality > 0) ||
                   acceptedEncodings.Any(e => e.Encoding == "*" && e.Quality > 0);
        }

        foreach (var encoding in responseEncodings)
        {
            foreach (var accepted in acceptedEncodings.Where(a => a.Quality > 0))
            {
                if (accepted.Encoding == "*")
                    return true;

                if (string.Equals(accepted.Encoding, encoding, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Generates a variant key for caching multiple representations of the same resource.
    /// </summary>
    public string GenerateVariantKey(HttpRequestMessage request, IEnumerable<string> varyHeaders)
    {
        var parts = new List<string>();

        foreach (var header in varyHeaders)
        {
            if (header.Equals("Accept", StringComparison.OrdinalIgnoreCase))
            {
                var accept = string.Join(",", request.Headers.Accept?.Select(a => a.ToString()) ?? Enumerable.Empty<string>());
                parts.Add($"accept:{accept}");
            }
            else if (header.Equals("Accept-Language", StringComparison.OrdinalIgnoreCase))
            {
                var lang = string.Join(",", request.Headers.AcceptLanguage?.Select(l => l.ToString()) ?? Enumerable.Empty<string>());
                parts.Add($"lang:{lang}");
            }
            else if (header.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                var enc = string.Join(",", request.Headers.AcceptEncoding?.Select(e => e.ToString()) ?? Enumerable.Empty<string>());
                parts.Add($"enc:{enc}");
            }
            else if (header.Equals("Accept-Charset", StringComparison.OrdinalIgnoreCase))
            {
                var charset = string.Join(",", request.Headers.AcceptCharset?.Select(c => c.ToString()) ?? Enumerable.Empty<string>());
                parts.Add($"charset:{charset}");
            }
            else if (request.Headers.TryGetValues(header, out var values))
            {
                var value = string.Join(",", values);
                parts.Add($"{header.ToLower()}:{value}");
            }
        }

        return string.Join("|", parts);
    }

    /// <summary>
    /// Selects the best variant from cached alternatives based on accept preferences.
    /// </summary>
    public HttpCacheEntry? SelectBestVariant(
        IEnumerable<HttpCacheEntry> variants,
        AcceptedContent acceptedContent)
    {
        HttpCacheEntry? bestMatch = null;
        double bestScore = 0;

        foreach (var variant in variants)
        {
            var score = CalculateVariantScore(variant, acceptedContent);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = variant;
            }
        }

        return bestMatch;
    }

    private double CalculateVariantScore(HttpCacheEntry variant, AcceptedContent acceptedContent)
    {
        double score = 0;

        using var response = variant.ToHttpResponse();

        // Score based on content type match and quality
        if (response.Content?.Headers.ContentType != null)
        {
            var contentType = response.Content.Headers.ContentType.MediaType;
            var matchingType = acceptedContent.ContentTypes
                .FirstOrDefault(t => IsMediaTypeMatch(contentType, t.MediaType));

            if (matchingType != null)
            {
                score += matchingType.Quality * 100; // Content type is most important
            }
        }

        // Score based on language match
        if (_options.EnableAcceptLanguage && response.Content?.Headers.ContentLanguage != null)
        {
            var languages = response.Content.Headers.ContentLanguage;
            var bestLangScore = 0.0;

            foreach (var lang in languages)
            {
                var matchingLang = acceptedContent.Languages
                    .FirstOrDefault(l => lang.StartsWith(l.Language, StringComparison.OrdinalIgnoreCase));

                if (matchingLang != null)
                {
                    bestLangScore = Math.Max(bestLangScore, matchingLang.Quality * 10);
                }
            }

            score += bestLangScore;
        }

        // Score based on encoding
        if (_options.EnableAcceptEncoding && response.Content?.Headers.ContentEncoding != null)
        {
            var encodings = response.Content.Headers.ContentEncoding;
            var bestEncScore = 0.0;

            foreach (var enc in encodings)
            {
                var matchingEnc = acceptedContent.Encodings
                    .FirstOrDefault(e => string.Equals(e.Encoding, enc, StringComparison.OrdinalIgnoreCase));

                if (matchingEnc != null)
                {
                    bestEncScore = Math.Max(bestEncScore, matchingEnc.Quality * 5);
                }
            }

            score += bestEncScore;
        }

        return score;
    }

    private bool IsMediaTypeMatch(string? responseType, string acceptType)
    {
        if (string.IsNullOrEmpty(responseType))
            return false;

        if (acceptType == "*/*")
            return true;

        if (acceptType.EndsWith("/*"))
        {
            var prefix = acceptType.Substring(0, acceptType.Length - 2);
            return responseType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(acceptType, responseType, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Represents accepted content preferences from HTTP headers.
/// </summary>
public class AcceptedContent
{
    public List<MediaTypeWithQuality> ContentTypes { get; set; } = new();
    public List<LanguageWithQuality> Languages { get; set; } = new();
    public List<EncodingWithQuality> Encodings { get; set; } = new();
    public List<CharsetWithQuality> Charsets { get; set; } = new();
}

/// <summary>
/// Media type with quality value.
/// </summary>
public class MediaTypeWithQuality
{
    public string MediaType { get; set; } = "*/*";
    public double Quality { get; set; } = 1.0;
    public Dictionary<string, string?> Parameters { get; set; } = new();
}

/// <summary>
/// Language with quality value.
/// </summary>
public class LanguageWithQuality
{
    public string Language { get; set; } = "*";
    public double Quality { get; set; } = 1.0;
}

/// <summary>
/// Encoding with quality value.
/// </summary>
public class EncodingWithQuality
{
    public string Encoding { get; set; } = "identity";
    public double Quality { get; set; } = 1.0;
}

/// <summary>
/// Charset with quality value.
/// </summary>
public class CharsetWithQuality
{
    public string Charset { get; set; } = "utf-8";
    public double Quality { get; set; } = 1.0;
}