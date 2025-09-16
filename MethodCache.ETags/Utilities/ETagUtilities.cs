using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MethodCache.ETags.Utilities
{
    /// <summary>
    /// Utility methods for ETag generation and validation.
    /// </summary>
    public static class ETagUtilities
    {
        /// <summary>
        /// Generates an ETag from content using SHA256 hash.
        /// </summary>
        /// <param name="content">Content to hash</param>
        /// <param name="useWeakETag">Whether to generate a weak ETag</param>
        /// <returns>Generated ETag</returns>
        public static string GenerateETag(byte[] content, bool useWeakETag = false)
        {
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(content);
            var etag = Convert.ToBase64String(hash);
            
            return useWeakETag ? $"W/\"{etag}\"" : $"\"{etag}\"";
        }

        /// <summary>
        /// Generates an ETag from an object by serializing it.
        /// </summary>
        /// <param name="obj">Object to generate ETag for</param>
        /// <param name="useWeakETag">Whether to generate a weak ETag</param>
        /// <returns>Generated ETag</returns>
        public static string GenerateETag(object obj, bool useWeakETag = false)
        {
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            return GenerateETag(bytes, useWeakETag);
        }

        /// <summary>
        /// Generates an ETag from a string.
        /// </summary>
        /// <param name="content">String content</param>
        /// <param name="useWeakETag">Whether to generate a weak ETag</param>
        /// <returns>Generated ETag</returns>
        public static string GenerateETag(string content, bool useWeakETag = false)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            return GenerateETag(bytes, useWeakETag);
        }

        /// <summary>
        /// Generates an ETag based on last modified timestamp.
        /// </summary>
        /// <param name="lastModified">Last modified timestamp</param>
        /// <param name="useWeakETag">Whether to generate a weak ETag</param>
        /// <returns>Generated ETag</returns>
        public static string GenerateETagFromTimestamp(DateTime lastModified, bool useWeakETag = false)
        {
            var ticks = lastModified.Ticks.ToString();
            var etag = Convert.ToBase64String(Encoding.UTF8.GetBytes(ticks));
            
            return useWeakETag ? $"W/\"{etag}\"" : $"\"{etag}\"";
        }

        /// <summary>
        /// Generates an ETag from a version number.
        /// </summary>
        /// <param name="version">Version number</param>
        /// <param name="useWeakETag">Whether to generate a weak ETag</param>
        /// <returns>Generated ETag</returns>
        public static string GenerateETagFromVersion(long version, bool useWeakETag = false)
        {
            var etag = Convert.ToBase64String(Encoding.UTF8.GetBytes(version.ToString()));
            return useWeakETag ? $"W/\"{etag}\"" : $"\"{etag}\"";
        }

        /// <summary>
        /// Combines multiple values to generate a composite ETag.
        /// </summary>
        /// <param name="values">Values to combine</param>
        /// <param name="useWeakETag">Whether to generate a weak ETag</param>
        /// <returns>Generated composite ETag</returns>
        public static string GenerateCompositeETag(IEnumerable<object> values, bool useWeakETag = false)
        {
            var combined = string.Join("|", values.Select(v => v?.ToString() ?? "null"));
            return GenerateETag(combined, useWeakETag);
        }

        /// <summary>
        /// Validates an ETag format.
        /// </summary>
        /// <param name="etag">ETag to validate</param>
        /// <returns>True if valid ETag format</returns>
        public static bool IsValidETag(string? etag)
        {
            if (string.IsNullOrEmpty(etag))
                return false;

            // Check for weak ETag format: W/"..."
            if (etag.StartsWith("W/\"") && etag.EndsWith("\"") && etag.Length > 4)
                return true;

            // Check for strong ETag format: "..."
            if (etag.StartsWith("\"") && etag.EndsWith("\"") && etag.Length > 2)
                return true;

            return false;
        }

        /// <summary>
        /// Extracts the ETag value without quotes and weak indicator.
        /// </summary>
        /// <param name="etag">Full ETag string</param>
        /// <returns>ETag value without formatting</returns>
        public static string? ExtractETagValue(string? etag)
        {
            if (string.IsNullOrEmpty(etag))
                return null;

            // Remove W/ prefix if present
            if (etag.StartsWith("W/"))
                etag = etag.Substring(2);

            // Remove quotes
            if (etag.StartsWith("\"") && etag.EndsWith("\""))
                etag = etag.Substring(1, etag.Length - 2);

            return etag;
        }

        /// <summary>
        /// Checks if an ETag is weak.
        /// </summary>
        /// <param name="etag">ETag to check</param>
        /// <returns>True if weak ETag</returns>
        public static bool IsWeakETag(string? etag)
        {
            return etag?.StartsWith("W/") == true;
        }

        /// <summary>
        /// Compares two ETags for equality, handling weak/strong comparison rules.
        /// </summary>
        /// <param name="etag1">First ETag</param>
        /// <param name="etag2">Second ETag</param>
        /// <param name="strongComparison">Whether to use strong comparison (both must be strong)</param>
        /// <returns>True if ETags match</returns>
        public static bool ETagsMatch(string? etag1, string? etag2, bool strongComparison = false)
        {
            if (string.IsNullOrEmpty(etag1) || string.IsNullOrEmpty(etag2))
                return false;

            // For strong comparison, both ETags must be strong
            if (strongComparison && (IsWeakETag(etag1) || IsWeakETag(etag2)))
                return false;

            // Compare the actual values
            var value1 = ExtractETagValue(etag1);
            var value2 = ExtractETagValue(etag2);

            return string.Equals(value1, value2, StringComparison.Ordinal);
        }

        /// <summary>
        /// Parses If-None-Match header value to extract ETags.
        /// </summary>
        /// <param name="ifNoneMatch">If-None-Match header value</param>
        /// <returns>Collection of ETags</returns>
        public static IEnumerable<string> ParseIfNoneMatch(string? ifNoneMatch)
        {
            if (string.IsNullOrEmpty(ifNoneMatch))
                yield break;

            // Handle "*" case
            if (ifNoneMatch.Trim() == "*")
            {
                yield return "*";
                yield break;
            }

            // Split on commas and clean up
            var etags = ifNoneMatch.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var etag in etags)
            {
                var cleanETag = etag.Trim();
                if (IsValidETag(cleanETag))
                {
                    yield return cleanETag;
                }
            }
        }
    }
}