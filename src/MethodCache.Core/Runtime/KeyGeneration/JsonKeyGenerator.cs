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
/// Keys have the form <c>{methodName}[:v{version}]:[42,"alice",{"Page":2}]:{types}</c>, so all entries of one method
/// share the prefix that pattern invalidation matches (see <see cref="MethodCacheKeys"/>). <c>{types}</c> is a short
/// hash of the arguments' runtime types, so <c>1</c> and <c>1L</c>, which write the same JSON, get different keys.
/// An argument list longer than
/// <see cref="MaxReadableLength"/> characters is replaced by its SHA-256, so keys stay within the limits of stores
/// such as SQL Server. Arguments are serialized with System.Text.Json, public fields included (a value inside them
/// whose runtime type differs from its declared type is written with that type's name), and an
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
        var types = TypeSuffix(args);

        return policy.Version is { } version
            ? string.Concat(methodName, ":v", version.ToString(CultureInfo.InvariantCulture), ":", arguments, ":", types)
            : string.Concat(methodName, ":", arguments, ":", types);
    }

    /// <summary>
    /// A short hash of the arguments' runtime types, so arguments that write the same JSON (<c>1</c> and <c>1L</c>,
    /// or two classes with the same properties) still get different keys.
    /// </summary>
    private static string TypeSuffix(object?[] args)
    {
        var names = new StringBuilder();
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                names.Append('\n');
            }

            if (args[i] is { } arg)
            {
                names.Append(KeyArguments.TypeName(arg.GetType()));
            }
            else
            {
                names.Append("null");
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(names.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
