using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// Storage keys: <c>{client}:{hash of method and URI}</c> for a URL, and
/// <c>{URL key}|{hash of the variant generation and the selecting header values}</c> for each of its variants.
/// Everything that tells callers apart (key headers, <c>Authorization</c> and <c>Cookie</c> in a private cache,
/// <c>Vary</c>) lives in the variant key, so removing the URL entry invalidates the URL for every caller.
/// </summary>
internal static class HttpCacheKeys
{
    public static string ForGeneration(string urlKey, string generation)
        => generation.Length == 0 ? urlKey : urlKey + "|generation:" + generation;

    public static string ForRequest(string clientName, HttpRequestMessage request)
        => ForUri(clientName, request.Method, request.RequestUri);

    public static string ForUri(string clientName, HttpMethod method, Uri? uri)
        => ForUriWithIdentity(ClientIdentity(clientName), method, uri);

    internal static string ClientIdentity(string clientName)
        => Convert.ToHexString(SHA256.HashData(MethodCache.Infrastructure.LosslessUtf16.GetBytes(clientName)));

    internal static string ForRequestWithIdentity(string clientIdentity, HttpRequestMessage request)
        => ForUriWithIdentity(clientIdentity, request.Method, request.RequestUri);

    private static string ForUriWithIdentity(string client, HttpMethod method, Uri? uri)
    {
        // Preserve the frozen length-prefixed UTF-8 URI frame exactly; only temporary buffers change.
        var address = uri == null ? string.Empty : uri.IsAbsoluteUri ? uri.GetComponents(UriComponents.HttpRequestUrl | UriComponents.UserInfo, UriFormat.UriEscaped) : uri.ToString();
        var length = Encoding.UTF8.GetByteCount(address);
        byte[]? rented = null;
        Span<byte> frame = length <= 508 ? stackalloc byte[length + 4] : (rented = ArrayPool<byte>.Shared.Rent(checked(length + 4))).AsSpan(0, length + 4);
        Span<byte> digest = stackalloc byte[32];
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(frame, length);
            _ = Encoding.UTF8.GetBytes(address.AsSpan(), frame[4..]);
            _ = SHA256.HashData(frame, digest);
        }
        finally
        {
            // URI user info and queries must not remain in a pooled buffer.
            if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
        return string.Create(client.Length + 48 + method.Method.Length,
            (Client: client, Low: BinaryPrimitives.ReadUInt128LittleEndian(digest), High: BinaryPrimitives.ReadUInt128LittleEndian(digest[16..]), Method: method.Method),
            static (text, state) =>
            {
                state.Client.AsSpan().CopyTo(text);
                var offset = state.Client.Length;
                text[offset++] = ':';
                Span<byte> bytes = stackalloc byte[32];
                BinaryPrimitives.WriteUInt128LittleEndian(bytes, state.Low);
                BinaryPrimitives.WriteUInt128LittleEndian(bytes[16..], state.High);
                _ = Convert.TryToBase64Chars(bytes, text.Slice(offset, 44), out _);
                offset += 44;
                ":m:".AsSpan().CopyTo(text[offset..]);
                state.Method.AsSpan().CopyTo(text[(offset + 3)..]);
            });
    }

    internal static string GenerationTarget(string key)
    {
        var target = GenerationTargetSpan(key);
        return target.Length == key.Length ? key : new string(target);
    }

    internal static ReadOnlySpan<char> GenerationTargetSpan(string key)
    {
        var method = key.IndexOf(":m:", StringComparison.Ordinal);
        return method < 0 ? key.AsSpan() : key.AsSpan(0, method);
    }

    /// <summary>
    /// The request headers that select a variant of a response: the configured key headers, <c>Authorization</c>
    /// and <c>Cookie</c> in a private cache (so callers never see each other's responses) and the response's <c>Vary</c> headers.
    /// Empty when the URL has a single response for everyone.
    /// </summary>
    public static string[] SelectingHeaders(HttpCacheOptions options, string[]? varyHeaders)
    {
        var headers = new HashSet<string>(options.KeyHeaders, StringComparer.OrdinalIgnoreCase);
        if (!options.IsSharedCache)
        {
            // Who the caller is: a bearer token, or a session cookie
            headers.Add("Authorization");
            headers.Add("Cookie");
        }

        if (varyHeaders != null)
        {
            headers.UnionWith(varyHeaders);
        }

        return headers.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Concurrent first writers with the same selection rules share one namespace within the URL epoch.</summary>
    public static string ForSelectingHeaders(string[] selectingHeaders)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var header in selectingHeaders.OrderBy(static h => h, StringComparer.OrdinalIgnoreCase))
            Append(hash, header.ToLowerInvariant());
        return Convert.ToHexString(hash.GetHashAndReset());
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

    /// <summary>Whether the request sends any of <paramref name="headers"/>.</summary>
    public static bool SendsAny(HttpRequestMessage request, string[] headers)
    {
        foreach (var header in headers)
        {
            if (request.Headers.TryGetValues(header, out _) || request.Content?.Headers.TryGetValues(header, out _) == true)
            {
                return true;
            }
        }

        return false;
    }

    private static void AppendHeader(IncrementalHash hash, HttpRequestMessage request, string header)
    {
        Append(hash, header.ToLowerInvariant());

        // A header sent with an empty value and a header not sent at all are different requests
        if (request.Headers.TryGetValues(header, out var values) ||
            request.Content?.Headers.TryGetValues(header, out values) == true)
        {
            Append(hash, "1");
            var items = values.ToArray();
            Append(hash, items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in items)
            {
                Append(hash, value);
            }
        }
        else
        {
            Append(hash, "0");
        }
    }

    private static void Append(IncrementalHash hash, string value)
        => AppendBytes(hash, MethodCache.Infrastructure.LosslessUtf16.GetBytes(value));

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
