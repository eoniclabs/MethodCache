using System.Security.Cryptography;
using System.Text;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// Storage keys: <c>{client}:{hash of method, URI, key headers and caller}</c> for a URL, and
/// <c>{base key}|{hash of the Vary header values}</c> for each of its variants.
/// </summary>
internal static class HttpCacheKeys
{
    public static string ForRequest(string clientName, HttpRequestMessage request, HttpCacheOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, request.Method.Method);
        Append(hash, request.RequestUri?.ToString() ?? string.Empty);

        foreach (var header in options.KeyHeaders.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            AppendHeader(hash, request, header);
        }

        // A private cache keeps each caller's responses apart
        if (!options.IsSharedCache && request.Headers.Authorization is { } authorization)
        {
            Append(hash, authorization.ToString());
        }

        return clientName + ":" + Convert.ToBase64String(hash.GetHashAndReset());
    }

    /// <summary>
    /// The variant key starts with the base key, so a variant is only found through its own URL, method and caller.
    /// Returns <c>null</c> for <c>Vary: *</c>, which is never served from cache.
    /// </summary>
    public static string? ForVariant(string baseKey, HttpRequestMessage request, string[] varyHeaders)
    {
        if (varyHeaders.Contains("*", StringComparer.Ordinal))
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var header in varyHeaders.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
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
