using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MessagePack;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;

namespace MethodCache.Serialization.MessagePack;

/// <summary>
/// Cache key generator that hashes arguments' MessagePack form with SHA-256.
/// </summary>
/// <remarks>
/// Use it when arguments serialize with MessagePack but not with System.Text.Json, which the default
/// <see cref="FastHashKeyGenerator"/> uses. Keys have the same form, <c>{methodName}[:v{version}]:{hash}</c>, so
/// invalidating all of a method's entries works the same way. Keys embed the arguments' assembly-qualified type
/// names, so they change when those assemblies' versions do.
/// </remarks>
/// <example>
/// <code>
/// services.AddMethodCache();
/// services.Replace(ServiceDescriptor.Singleton&lt;ICacheKeyGenerator, MessagePackKeyGenerator&gt;());
/// </code>
/// </example>
public sealed class MessagePackKeyGenerator : ICacheKeyGenerator
{
    private const byte NullFrame = 0x00;
    private const byte KeyProviderFrame = 0x01;
    private const byte SerializedFrame = 0x02;

    public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(methodName);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(policy);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> number = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(number, policy.Version ?? -1);
        hash.AppendData(number);
        BinaryPrimitives.WriteInt32LittleEndian(number, args.Length);
        hash.AppendData(number);

        foreach (var arg in args)
        {
            switch (arg)
            {
                case null:
                    AppendFrame(hash, NullFrame, ReadOnlySpan<byte>.Empty);
                    break;
                case ICacheKeyProvider keyProvider:
                    AppendFrame(hash, KeyProviderFrame, Encoding.UTF8.GetBytes(keyProvider.CacheKeyPart ?? string.Empty));
                    break;
                default:
                    AppendFrame(hash, SerializedFrame, MessagePackSerializer.Typeless.Serialize(arg));
                    break;
            }
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return policy.Version is { } version
            ? string.Concat(methodName, ":v", version.ToString(CultureInfo.InvariantCulture), ":", digest)
            : string.Concat(methodName, ":", digest);
    }

    private static void AppendFrame(IncrementalHash hash, byte frameType, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[1 + sizeof(int)];
        header[0] = frameType;
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(1), payload.Length);
        hash.AppendData(header);
        hash.AppendData(payload);
    }
}
