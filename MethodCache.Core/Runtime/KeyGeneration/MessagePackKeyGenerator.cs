using System.Security.Cryptography;
using System.Text;
using MessagePack;
using MethodCache.Core.Runtime.Core;

namespace MethodCache.Core.Runtime.KeyGeneration;

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
    private const byte KeyProviderFrame = 0x01;
    private const byte SerializedFrame = 0x02;

    public string GenerateKey(string methodName, object[] args, CacheRuntimePolicy policy)
        => GenerateKeyInternal(methodName, args, policy.Version);

    private static string GenerateKeyInternal(string methodName, object[] args, int? version)
    {
        using (var sha256 = SHA256.Create())
        {
            // Hash method name
            var methodBytes = Encoding.UTF8.GetBytes(methodName);
            sha256.TransformBlock(methodBytes, 0, methodBytes.Length, null, 0);

            // Hash version if present
            if (version.HasValue)
            {
                var versionBytes = BitConverter.GetBytes(version.Value);
                sha256.TransformBlock(versionBytes, 0, versionBytes.Length, null, 0);
            }

            // Hash arguments
            foreach (var arg in args)
            {
                if (arg is ICacheKeyProvider keyProvider)
                {
                    var keyPartBytes = Encoding.UTF8.GetBytes(keyProvider.CacheKeyPart);
                    HashFramedPayload(sha256, KeyProviderFrame, keyPartBytes);
                }
                else
                {
                    // Serialize and hash the bytes directly
                    var serializedArg = MessagePackSerializer.Typeless.Serialize(arg);
                    HashFramedPayload(sha256, SerializedFrame, serializedArg);
                }
            }

            // Finalize hash
            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToBase64String(sha256.Hash!);
        }
    }

    private static void HashFramedPayload(SHA256 sha256, byte frameType, byte[] payload)
    {
        var frameHeader = new byte[5];
        frameHeader[0] = frameType;
        var payloadLength = BitConverter.GetBytes(payload.Length);
        Buffer.BlockCopy(payloadLength, 0, frameHeader, 1, payloadLength.Length);

        sha256.TransformBlock(frameHeader, 0, frameHeader.Length, null, 0);
        sha256.TransformBlock(payload, 0, payload.Length, null, 0);
    }
}
