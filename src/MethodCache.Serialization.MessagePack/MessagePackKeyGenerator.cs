using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using MessagePack;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;

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
    private const byte NullKeyProviderFrame = 0x03;
    private static readonly KeyObjectFormatter ObjectFormatter = new();
    private static readonly MessagePackSerializerOptions KeyOptions = TypelessContractlessStandardResolver.Options
        .WithResolver(CompositeResolver.Create(
            new IMessagePackFormatter[] { new KeyStringFormatter(), ObjectFormatter },
            new IFormatterResolver[] { new PolymorphicKeyResolver() }));

    /// <inheritdoc />
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
                    if (keyProvider.CacheKeyPart is { } part)
                        AppendFrame(hash, KeyProviderFrame, Utf16Bytes(part));
                    else
                        AppendFrame(hash, NullKeyProviderFrame, ReadOnlySpan<byte>.Empty);
                    break;
                default:
                    AppendFrame(hash, SerializedFrame, MessagePackSerializer.Serialize<object>(arg, KeyOptions));
                    break;
            }
        }

        var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return policy.Version is { } version
            ? string.Concat(methodName, ":v", version.ToString(CultureInfo.InvariantCulture), ":", digest)
            : string.Concat(methodName, ":", digest);
    }

    private static byte[] Utf16Bytes(string value) => LosslessUtf16.GetBytes(value);

    // A formatter for a declared base/interface slot must consult the runtime type before the
    // contractless formatter discards derived members. Nested members retain these same options.
    private sealed class PolymorphicKeyResolver : IFormatterResolver
    {
        public IMessagePackFormatter<T>? GetFormatter<T>() => FormatterCache<T>.Instance;

        private static class FormatterCache<T>
        {
            internal static readonly IMessagePackFormatter<T>? Instance =
                !typeof(T).IsValueType && !typeof(T).IsSealed && typeof(T) != typeof(Type)
                    ? new PolymorphicKeyFormatter<T>()
                    : TypelessContractlessStandardResolver.Instance.GetFormatter<T>();
        }
    }

    private sealed class PolymorphicKeyFormatter<T> : IMessagePackFormatter<T>
    {
        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
            }
            else if (value.GetType() != typeof(T))
            {
                ObjectFormatter.Serialize(ref writer, value, options);
            }
            else
            {
                TypelessContractlessStandardResolver.Instance.GetFormatterWithVerify<T>()
                    .Serialize(ref writer, value, options);
            }
        }

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
            throw new NotSupportedException("Cache keys are hashed, never deserialized.");
    }

    // Only the key serializer uses these formatters. Cached values keep the application's MessagePack format.
    internal sealed class KeyStringFormatter : IMessagePackFormatter<string?>
    {
        public void Serialize(ref MessagePackWriter writer, string? value, MessagePackSerializerOptions options)
        {
            if (value is null) writer.WriteNil();
            else writer.WriteExtensionFormat(new ExtensionResult(42, Utf16Bytes(value)));
        }
        public string? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
            throw new NotSupportedException("Cache keys are hashed, never deserialized.");
    }

    internal sealed class KeyObjectFormatter : IMessagePackFormatter<object?>
    {
        public void Serialize(ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options)
        {
            if (value is string text)
                options.Resolver.GetFormatterWithVerify<string?>().Serialize(ref writer, text, options);
            else
                TypelessFormatter.Instance.Serialize(ref writer, value, options);
        }
        public object? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
            throw new NotSupportedException("Cache keys are hashed, never deserialized.");
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
