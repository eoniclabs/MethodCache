using System.Security.Cryptography;
using System.Text;

namespace MethodCache.HttpCaching.Validation;

public class VaryHeaderCacheKeyGenerator
{
    public string GenerateKey(HttpRequestMessage request, string[] varyHeaders)
    {
        if (varyHeaders.Contains("*", StringComparer.OrdinalIgnoreCase))
        {
            return "UNCACHEABLE:*";
        }

        // Use hashing for compact, deterministic keys
        using var sha256 = SHA256.Create();

        // Hash method
        var methodBytes = Encoding.UTF8.GetBytes(request.Method.Method);
        sha256.TransformBlock(methodBytes, 0, methodBytes.Length, null, 0);

        // Hash URI
        var uriBytes = Encoding.UTF8.GetBytes(request.RequestUri?.ToString() ?? string.Empty);
        sha256.TransformBlock(uriBytes, 0, uriBytes.Length, null, 0);

        // Hash vary headers
        foreach (var varyHeader in varyHeaders)
        {
            var normalizedHeader = NormalizeHeaderName(varyHeader);
            var headerNameBytes = Encoding.UTF8.GetBytes(normalizedHeader);
            sha256.TransformBlock(headerNameBytes, 0, headerNameBytes.Length, null, 0);

            var headerValue = GetHeaderValue(request, varyHeader);

            // Hash authorization headers for privacy
            if (string.Equals(varyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                headerValue = HashValue(headerValue);
            }

            var headerValueBytes = Encoding.UTF8.GetBytes(headerValue);
            sha256.TransformBlock(headerValueBytes, 0, headerValueBytes.Length, null, 0);
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToBase64String(sha256.Hash!);
    }

    private static string GetHeaderValue(HttpRequestMessage request, string headerName)
    {
        // Try request headers first
        if (request.Headers.TryGetValues(headerName, out var requestValues))
        {
            return string.Join(", ", requestValues);
        }

        // Try content headers if available
        if (request.Content?.Headers.TryGetValues(headerName, out var contentValues) == true)
        {
            return string.Join(", ", contentValues);
        }

        return string.Empty;
    }

    private static string NormalizeHeaderName(string headerName)
    {
        if (string.IsNullOrEmpty(headerName))
        {
            return string.Empty;
        }

        // Use Span to avoid allocations for small header names; fall back to heap for large ones
        const int StackAllocThreshold = 128;
        Span<char> buffer = headerName.Length <= StackAllocThreshold
            ? stackalloc char[headerName.Length]
            : new char[headerName.Length];
        headerName.AsSpan().CopyTo(buffer);

        var capitalizeNext = true;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '-')
            {
                capitalizeNext = true;
            }
            else if (capitalizeNext)
            {
                buffer[i] = char.ToUpperInvariant(buffer[i]);
                capitalizeNext = false;
            }
            else
            {
                buffer[i] = char.ToLowerInvariant(buffer[i]);
            }
        }

        return new string(buffer);
    }

    private static string HashValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..16]; // Use first 16 characters for brevity
    }
}