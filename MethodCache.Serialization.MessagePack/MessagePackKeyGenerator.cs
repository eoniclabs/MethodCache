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
/// Generated decorators format simple arguments and records of simple values themselves; the key generator only
/// sees the other arguments, such as collections and nested objects. Use this one when those arguments serialize
/// with MessagePack but not with System.Text.Json, which the default <see cref="FastHashKeyGenerator"/> uses.
/// Keys embed the arguments' assembly-qualified type names, so they change when those assemblies' versions do.
/// </remarks>
/// <example>
/// <code>
/// services.AddMethodCache();
/// services.Replace(ServiceDescriptor.Singleton&lt;ICacheKeyGenerator, MessagePackKeyGenerator&gt;());
/// </code>
/// </example>
public sealed class MessagePackKeyGenerator : ICacheKeyGenerator
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
