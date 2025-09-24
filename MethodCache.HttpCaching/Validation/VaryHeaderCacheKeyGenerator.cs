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

        var keyBuilder = new StringBuilder();
        keyBuilder.Append($"{request.Method}:{request.RequestUri}");

        foreach (var varyHeader in varyHeaders)
        {
            var headerValue = GetHeaderValue(request, varyHeader);

            // Hash authorization headers for privacy
            if (string.Equals(varyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                headerValue = HashValue(headerValue);
            }

            keyBuilder.Append($":{NormalizeHeaderName(varyHeader)}={headerValue}");
        }

        return keyBuilder.ToString();
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
        // Normalize to proper case (first letter and letters after hyphens capitalized)
        var parts = headerName.Split('-');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                parts[i] = parts[i].Length == 1
                    ? char.ToUpper(parts[i][0]).ToString()
                    : char.ToUpper(parts[i][0]) + parts[i][1..].ToLower();
            }
        }
        return string.Join("-", parts);
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