using System.Security.Cryptography;
using System.Text;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// Storage keys: <c>{client}:{hash of method and URI}</c> for a URL, and
/// <c>{URL key}|{hash of the variant generation and the selecting header values}</c> for each of its variants.
/// Everything that tells callers apart (key headers, <c>Authorization</c> in a private cache, <c>Vary</c>) lives in
/// the variant key, so removing the URL entry invalidates the URL for every caller.
/// </summary>
internal static class HttpCacheKeys
{
    public static string ForRequest(string clientName, HttpRequestMessage request)
        => ForUri(clientName, request.Method, request.RequestUri);

    public static string ForUri(string clientName, HttpMethod method, Uri? uri)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, method.Method);
        Append(hash, uri?.ToString() ?? string.Empty);
        return clientName + ":" + Convert.ToBase64String(hash.GetHashAndReset());
    }

    /// <summary>
    /// The request headers that select a variant of a response: the configured key headers, <c>Authorization</c>
    /// in a private cache (so callers never see each other's responses) and the response's <c>Vary</c> headers.
    /// Empty when the URL has a single response for everyone.
    /// </summary>
    public static string[] SelectingHeaders(HttpCacheOptions options, string[]? varyHeaders)
    {
        var headers = new HashSet<string>(options.KeyHeaders, StringComparer.OrdinalIgnoreCase);
        if (!options.IsSharedCache)
        {
            headers.Add("Authorization");
        }

        if (varyHeaders != null)
        {
            headers.UnionWith(varyHeaders);
        }

        return headers.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// The variant key starts with the URL key, so a variant is only found through its own URL and method.
    /// Returns <c>null</c> for <c>Vary: *</c>, which is never served from cache.
    /// </summary>
    public static string? ForVariant(string baseKey, string generation, HttpRequestMessage request, string[] selectingHeaders)
    {
        if (selectingHeaders.Contains("*", StringComparer.Ordinal))
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, generation);
        foreach (var header in selectingHeaders.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            AppendHeader(hash, request, header);
        }

        return baseKey + "|" + Convert.ToBase64String(hash.GetHashAndReset());
    }

    private static void AppendHeader(IncrementalHash hash, HttpRequestMessage request, string header)
    {
        Append(hash, header.ToLowerInvariant());

        if (request.Headers.TryGetValues(header, out var values) ||
            request.Content?.Headers.TryGetValues(header, out values) == true)
        {
            Append(hash, string.Join(",", values));
        }
        else
        {
            Append(hash, string.Empty);
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]); // separator, so "ab" + "c" and "a" + "bc" hash differently
    }
}
