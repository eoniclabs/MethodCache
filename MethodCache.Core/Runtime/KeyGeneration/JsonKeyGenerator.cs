using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MethodCache.Core.Runtime.Core;

namespace MethodCache.Core.Runtime.KeyGeneration;

/// <summary>
/// Human-readable cache key generator using JSON serialization and SHA-256 hashing.
/// Best for development, debugging, and scenarios where cache key inspection is valuable.
/// </summary>
/// <remarks>
/// Performance characteristics:
/// - ~200ns per key generation (4x slower than FastHashKeyGenerator)
/// - Higher allocations due to JSON serialization
/// - Keys are Base64-encoded SHA-256 hashes of JSON-serialized arguments
///
/// Use JsonKeyGenerator when:
/// - Debugging cache issues and need to inspect keys
/// - Development environments where readability is more important than performance
/// - Need to serialize complex objects with reference preservation
///
/// For production high-throughput scenarios, prefer FastHashKeyGenerator.
/// For complex object serialization in production, consider MessagePackKeyGenerator.
/// </remarks>
/// <example>
/// <code>
/// // Configure globally for debugging
/// services.AddMethodCache(config =>
/// {
///     config.DefaultKeyGenerator&lt;JsonKeyGenerator&gt;();
/// });
///
/// // Or use per-method during development
/// [Cache(KeyGeneratorType = typeof(JsonKeyGenerator))]
/// Task&lt;Order&gt; GetOrderAsync(int orderId, bool includeDetails);
/// </code>
/// </example>
public class JsonKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheRuntimePolicy policy)
        => GenerateKeyInternal(methodName, args, policy.Version);

    private static string GenerateKeyInternal(string methodName, object[] args, int? version)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(methodName);

        if (version.HasValue)
        {
            keyBuilder.Append($"_v{version.Value}");
        }

        foreach (var arg in args)
        {
            if (arg is ICacheKeyProvider keyProvider)
            {
                keyBuilder.Append($"_{keyProvider.CacheKeyPart}");
            }
            else
            {
                // Use System.Text.Json for human-readable serialization
                var serializedArg = JsonSerializer.Serialize(arg,
                    new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve });
                keyBuilder.Append($"_{serializedArg}");
            }
        }

        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
            var base64Hash = Convert.ToBase64String(hash);
            if (version.HasValue)
            {
                return $"{base64Hash}_v{version.Value}";
            }
            return base64Hash;
        }
    }
}