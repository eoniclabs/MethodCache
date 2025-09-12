using System.Security.Cryptography;
using System.Text;
using MessagePack;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.KeyGenerators;

public class FastHashKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        var sb = new StringBuilder();
        sb.Append(methodName);

        if (settings.Version.HasValue) sb.Append($"_v{settings.Version.Value}");

        foreach (var arg in args)
        {
            if (arg is null)
            {
                sb.Append("_NULL");
            }
            else if (arg is ICacheKeyProvider keyProvider)
            {
                sb.Append($"_{keyProvider.CacheKeyPart}");
            }
            else if (arg is string s)
            {
                // SECURITY FIX: Use actual string value, not hash code
                // Escape underscores to prevent key collision attacks
                var escapedString = s.Replace("_", "__");
                sb.Append($"_STR:{escapedString}");
            }
            else if (arg is int i)
            {
                // For int, GetHashCode() returns the value itself, but be explicit
                sb.Append($"_INT:{i}");
            }
            else if (arg is long l)
            {
                // Use actual value, not hash code
                sb.Append($"_LONG:{l}");
            }
            else if (arg is bool b)
            {
                // Use actual value, not hash code
                sb.Append($"_BOOL:{b}");
            }
            else if (arg is double d)
            {
                // Use actual value, not hash code (with culture-invariant formatting)
                sb.Append($"_DBL:{d.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            else if (arg is float f)
            {
                // Use actual value, not hash code (with culture-invariant formatting)
                sb.Append($"_FLT:{f.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)}");
            }
            else if (arg is decimal dec)
            {
                // Handle decimal type for completeness
                sb.Append($"_DEC:{dec.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            else if (arg is DateTime dt)
            {
                // Handle DateTime with high precision
                sb.Append($"_DT:{dt.ToBinary()}");
            }
            else if (arg is DateTimeOffset dto)
            {
                // Handle DateTimeOffset
                sb.Append($"_DTO:{dto.ToUnixTimeMilliseconds()}:{dto.Offset.TotalMinutes}");
            }
            else if (arg is Guid guid)
            {
                // Handle Guid efficiently
                sb.Append($"_GUID:{guid:N}");
            }
            else if (arg is Enum enumValue)
            {
                // Handle enums by type and value
                sb.Append($"_ENUM:{enumValue.GetType().FullName}:{enumValue}");
            }
            else
            {
                // Fallback to MessagePack for complex types
                try
                {
                    var serializedArg = MessagePackSerializer.Typeless.Serialize(arg);
                    sb.Append($"_OBJ:{Convert.ToBase64String(serializedArg)}");
                }
                catch
                {
                    // Ultimate fallback: use type name + ToString()
                    // This is safer than GetHashCode() but may not be unique for all objects
                    var fallbackValue = $"{arg.GetType().FullName}:{arg.ToString()}";
                    var escapedFallback = fallbackValue.Replace("_", "__");
                    sb.Append($"_FALLBACK:{escapedFallback}");
                }
            }
        }

        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var base64Hash = Convert.ToBase64String(hash);
            if (settings.Version.HasValue) return $"{base64Hash}_v{settings.Version.Value}";
            return base64Hash;
        }
    }
}