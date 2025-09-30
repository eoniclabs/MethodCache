using System.Security.Cryptography;
using System.Text;
using MessagePack;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.KeyGenerators;

/// <summary>
/// Efficient cache key generator using MessagePack binary serialization and SHA-256 hashing.
/// Balanced performance for complex objects (~100ns) with deterministic serialization.
/// </summary>
/// <remarks>
/// Performance characteristics:
/// - ~100ns per key generation (2x slower than FastHashKeyGenerator, 2x faster than JsonKeyGenerator)
/// - Efficient binary serialization via MessagePack
/// - Deterministic output for identical objects
/// - Keys are Base64-encoded SHA-256 hashes
///
/// Use MessagePackKeyGenerator when:
/// - Caching methods with complex object parameters (DTOs, entities, nested objects)
/// - Need deterministic serialization of object graphs
/// - Balance between performance and serialization capability is important
/// - Working with objects that don't serialize well to JSON
///
/// For simple arguments (primitives, strings), FastHashKeyGenerator is faster.
/// For debugging, JsonKeyGenerator provides better readability.
/// </remarks>
/// <example>
/// <code>
/// // Configure for services with complex parameters
/// services.AddMethodCache(config =>
/// {
///     config.ForService&lt;IProductService&gt;()
///         .Method(s => s.SearchProductsAsync(default))
///         .WithKeyGenerator&lt;MessagePackKeyGenerator&gt;();
/// });
///
/// // Or use with attribute for complex search queries
/// [Cache(KeyGeneratorType = typeof(MessagePackKeyGenerator))]
/// Task&lt;SearchResults&gt; SearchProductsAsync(SearchCriteria criteria);
/// </code>
/// </example>
public class MessagePackKeyGenerator : ICacheKeyGenerator
{
    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append(methodName);

        if (settings.Version.HasValue) keyBuilder.Append($"_v{settings.Version.Value}");

        foreach (var arg in args)
            if (arg is ICacheKeyProvider keyProvider)
            {
                keyBuilder.Append($"_{keyProvider.CacheKeyPart}");
            }
            else
            {
                // Use MessagePack for deterministic serialization of complex objects
                var serializedArg = MessagePackSerializer.Typeless.Serialize(arg);
                keyBuilder.Append($"_{Convert.ToBase64String(serializedArg)}");
            }

        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(keyBuilder.ToString()));
            var base64Hash = Convert.ToBase64String(hash);
            if (settings.Version.HasValue) return $"{base64Hash}_v{settings.Version.Value}";
            return base64Hash;
        }
    }
}