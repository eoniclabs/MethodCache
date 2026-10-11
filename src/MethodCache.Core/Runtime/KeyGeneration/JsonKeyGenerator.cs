using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Infrastructure;

namespace MethodCache.Extensibility;

/// <summary>
/// Human-readable cache key generator: the method's key prefix followed by its arguments as a JSON array.
/// Best for development and debugging, when reading keys in the cache is worth more than speed.
/// </summary>
/// <remarks>
/// Keys have the form <c>{methodName}[:v{version}]:[42,"alice",{"Page":2}]</c>, so all entries of one method share
/// the prefix that pattern invalidation matches (see <see cref="MethodCacheKeys"/>). An argument list longer than
/// <see cref="MaxReadableLength"/> characters is replaced by its SHA-256, so keys stay within the limits of stores
/// such as SQL Server. Arguments are serialized with System.Text.Json, public fields included, and an
/// <see cref="ICacheKeyProvider"/> as its <see cref="ICacheKeyProvider.CacheKeyPart"/>.
/// For production, prefer the default <see cref="FastHashKeyGenerator"/>.
/// </remarks>
/// <example>
/// <code>
/// // Readable keys while debugging; the key generator applies to every cached method
/// services.AddMethodCache();
/// services.Replace(ServiceDescriptor.Singleton&lt;ICacheKeyGenerator, JsonKeyGenerator&gt;());
/// </code>
/// </example>
public class JsonKeyGenerator : ICacheKeyGenerator
{
    /// <summary>The longest argument list, in characters, that is written into the key as is.</summary>
    public const int MaxReadableLength = 200;

    /// <inheritdoc />
    public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var arg in args)
            {
                switch (arg)
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case ICacheKeyProvider keyProvider:
                        writer.WriteStringValue(keyProvider.CacheKeyPart);
                        break;
                    default:
                        writer.WriteRawValue(KeyArguments.Serialize(arg), skipInputValidation: true);
                        break;
                }
            }

            writer.WriteEndArray();
        }

        var arguments = buffer.WrittenCount <= MaxReadableLength
            ? Encoding.UTF8.GetString(buffer.WrittenSpan)
            : Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();

        return policy.Version is { } version
            ? string.Concat(methodName, ":v", version.ToString(CultureInfo.InvariantCulture), ":", arguments)
            : string.Concat(methodName, ":", arguments);
    }
}
