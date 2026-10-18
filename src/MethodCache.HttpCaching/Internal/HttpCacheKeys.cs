using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// Storage keys: <c>{hashed client}:{URI hash}:m:{method}</c> for a response, and a hash of the variant
/// generation and selecting headers for each variant. URL epoch targets omit the method suffix.
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
        return DigestKey(client, ':', digest, method.Method);
    }

    private static string DigestKey(string prefix, char separator, ReadOnlySpan<byte> digest, string? method = null)
        => string.Create(prefix.Length + 45 + (method == null ? 0 : 3 + method.Length),
            (Prefix: prefix, Separator: separator, Low: BinaryPrimitives.ReadUInt128LittleEndian(digest), High: BinaryPrimitives.ReadUInt128LittleEndian(digest[16..]), Method: method),
            static (text, state) =>
            {
                state.Prefix.AsSpan().CopyTo(text);
                var offset = state.Prefix.Length;
                text[offset++] = state.Separator;
                Span<byte> bytes = stackalloc byte[32];
                BinaryPrimitives.WriteUInt128LittleEndian(bytes, state.Low);
                BinaryPrimitives.WriteUInt128LittleEndian(bytes[16..], state.High);
                _ = Convert.TryToBase64Chars(bytes, text.Slice(offset, 44), out _);
                if (state.Method != null)
                {
                    offset += 44;
                    ":m:".AsSpan().CopyTo(text[offset..]);
                    state.Method.AsSpan().CopyTo(text[(offset + 3)..]);
                }
            });

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
        // The common schema is already ordered. Return a fresh private array so a
        // stored entry cannot mutate another response's caller-isolation headers.
        if (options.KeyHeaders.Count == 0 && varyHeaders is not { Length: > 0 })
            return options.IsSharedCache ? [] : ["Authorization", "Cookie"];

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

        var hash = new FramedHash(stackalloc byte[512]);
        try
        {
            hash.Append(generation);
            foreach (var header in selectingHeaders.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
                AppendHeader(ref hash, request, header);
            Span<byte> digest = stackalloc byte[32];
            hash.Finish(digest);
            return DigestKey(baseKey, '|', digest);
        }
        finally { hash.Dispose(); }
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

    private static void AppendHeader(ref FramedHash hash, HttpRequestMessage request, string header)
    {
        hash.Append(header.ToLowerInvariant());

        // A header sent with an empty value and a header not sent at all are different requests
        if (request.Headers.TryGetValues(header, out var values) ||
            request.Content?.Headers.TryGetValues(header, out values) == true)
        {
            hash.Append("1");
            var items = values.ToArray();
            hash.Append(items.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in items)
            {
                hash.Append(value);
            }
        }
        else
        {
            hash.Append("0");
        }
    }

    private static void Append(IncrementalHash hash, scoped ReadOnlySpan<char> value)
    {
        if (BitConverter.IsLittleEndian)
        {
            AppendBytes(hash, MemoryMarshal.AsBytes(value));
            return;
        }
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, checked(value.Length * 2));
        hash.AppendData(length);
        Span<byte> character = stackalloc byte[2];
        foreach (var item in value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(character, item);
            hash.AppendData(character);
        }
    }

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
    // Small frames use one SHA operation from the stack. Large frames switch to incremental hashing,
    // keeping working memory bounded instead of building a buffer proportional to caller header sizes.
    private ref struct FramedHash(Span<byte> buffer)
    {
        private Span<byte> _buffer = buffer;
        private IncrementalHash? _stream;
        private int _written;

        internal void Append(scoped ReadOnlySpan<char> value)
        {
            var bytes = checked(value.Length * 2);
            if (_stream == null && bytes <= _buffer.Length - _written - 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(_buffer[_written..], bytes);
                _written += 4;
                var destination = _buffer.Slice(_written, bytes);
                if (BitConverter.IsLittleEndian) MemoryMarshal.AsBytes(value).CopyTo(destination);
                else
                    for (var i = 0; i < value.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(destination[(i * 2)..], value[i]);
                _written += bytes;
                return;
            }
            if (_stream == null)
            {
                _stream = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _stream.AppendData(_buffer[.._written]);
                _buffer[.._written].Clear();
                _written = 0;
            }
            HttpCacheKeys.Append(_stream, value);
        }

        internal readonly void Finish(scoped Span<byte> digest)
        {
            if (_stream == null) _ = SHA256.HashData(_buffer[.._written], digest);
            else _ = _stream.TryGetHashAndReset(digest, out _);
        }
        internal void Dispose()
        {
            _stream?.Dispose(); _stream = null;
            _buffer[.._written].Clear();
            _buffer = default; _written = 0;
        }
    }
}
