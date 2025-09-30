using System.Net.Http.Headers;
using MethodCache.HttpCaching.Options;

namespace MethodCache.HttpCaching.Validation;

public class ContentNegotiationHandler
{
    private readonly CacheVariationOptions _options;

    public ContentNegotiationHandler(CacheVariationOptions options)
    {
        _options = options;
    }

    public AcceptedContent ParseAcceptHeader(HttpRequestMessage request)
    {
        var result = new AcceptedContent();

        if (request.Headers.Accept?.Any() == true)
        {
            result.ContentTypes = ParseMediaTypes(request.Headers.Accept);
        }

        if (_options.EnableAcceptLanguage && request.Headers.AcceptLanguage?.Any() == true)
        {
            result.Languages = ParseLanguages(request.Headers.AcceptLanguage);
        }

        if (_options.EnableAcceptEncoding && request.Headers.AcceptEncoding?.Any() == true)
        {
            result.Encodings = ParseEncodings(request.Headers.AcceptEncoding);
        }

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
                Quality = _options.EnableQualityValues ? header.Quality ?? 1.0 : 1.0,
                Parameters = header.Parameters?.ToDictionary(p => p.Name, p => p.Value) ?? new Dictionary<string, string?>()
            });
        }

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
                Quality = _options.EnableQualityValues ? header.Quality ?? 1.0 : 1.0
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
                Quality = _options.EnableQualityValues ? header.Quality ?? 1.0 : 1.0
            });
        }

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
                Quality = _options.EnableQualityValues ? header.Quality ?? 1.0 : 1.0
            });
        }

        return charsets.OrderByDescending(c => c.Quality).ToList();
    }

    public bool IsAcceptable(HttpCacheEntry cacheEntry, AcceptedContent acceptedContent)
    {
        using var cachedResponse = cacheEntry.ToHttpResponse();

        if (!IsContentTypeAcceptable(cachedResponse.Content?.Headers.ContentType, acceptedContent.ContentTypes))
        {
            return false;
        }

        if (_options.EnableAcceptLanguage && !IsLanguageAcceptable(cachedResponse.Content?.Headers.ContentLanguage, acceptedContent.Languages))
        {
            return false;
        }

        if (_options.EnableAcceptEncoding && !IsEncodingAcceptable(cachedResponse.Content?.Headers.ContentEncoding, acceptedContent.Encodings))
        {
            return false;
        }

        if (_options.EnableAcceptCharset && !IsCharsetAcceptable(cachedResponse.Content?.Headers.ContentType?.CharSet, acceptedContent.Charsets))
        {
            return false;
        }

        return true;
    }

    private static bool IsContentTypeAcceptable(MediaTypeHeaderValue? responseContentType, List<MediaTypeWithQuality> acceptedTypes)
    {
        if (responseContentType == null || acceptedTypes.Count == 0)
        {
            return true;
        }

        var responseType = responseContentType.MediaType;

        foreach (var accepted in acceptedTypes.Where(a => a.Quality > 0))
        {
            if (accepted.MediaType == "*/*")
            {
                return true;
            }

            if (accepted.MediaType.EndsWith("/*", StringComparison.Ordinal))
            {
                var prefix = accepted.MediaType[..^2];
                if (responseType?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
            else if (string.Equals(accepted.MediaType, responseType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLanguageAcceptable(ICollection<string>? responseLanguages, List<LanguageWithQuality> acceptedLanguages)
    {
        if (acceptedLanguages.Count == 0)
        {
            return true;
        }

        if (responseLanguages == null || responseLanguages.Count == 0)
        {
            return false;
        }

        foreach (var accepted in acceptedLanguages.Where(l => l.Quality > 0))
        {
            if (accepted.Language == "*" || responseLanguages.Contains(accepted.Language, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEncodingAcceptable(ICollection<string>? responseEncodings, List<EncodingWithQuality> acceptedEncodings)
    {
        if (acceptedEncodings.Count == 0)
        {
            return true;
        }

        if (responseEncodings == null || responseEncodings.Count == 0)
        {
            return acceptedEncodings.Any(e => e.Encoding == "identity");
        }

        foreach (var accepted in acceptedEncodings.Where(e => e.Quality > 0))
        {
            if (responseEncodings.Contains(accepted.Encoding, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCharsetAcceptable(string? responseCharset, List<CharsetWithQuality> acceptedCharsets)
    {
        if (acceptedCharsets.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(responseCharset))
        {
            return false;
        }

        return acceptedCharsets.Any(c => c.Quality > 0 && string.Equals(c.Charset, responseCharset, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class AcceptedContent
    {
        public List<MediaTypeWithQuality> ContentTypes { get; set; } = new();
        public List<LanguageWithQuality> Languages { get; set; } = new();
        public List<EncodingWithQuality> Encodings { get; set; } = new();
        public List<CharsetWithQuality> Charsets { get; set; } = new();
    }

    public sealed class MediaTypeWithQuality
    {
        public string MediaType { get; set; } = "*/*";
        public double Quality { get; set; } = 1.0;
        public Dictionary<string, string?> Parameters { get; set; } = new();
    }

    public sealed class LanguageWithQuality
    {
        public string Language { get; set; } = "*";
        public double Quality { get; set; } = 1.0;
    }

    public sealed class EncodingWithQuality
    {
        public string Encoding { get; set; } = "identity";
        public double Quality { get; set; } = 1.0;
    }

    public sealed class CharsetWithQuality
    {
        public string Charset { get; set; } = "utf-8";
        public double Quality { get; set; } = 1.0;
    }
}
