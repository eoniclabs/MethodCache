using System.Buffers;
using MessagePack;
using MessagePack.Resolvers;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Serialization.MessagePack;

/// <summary>
/// Writes cached values for the distributed cache with MessagePack: smaller and faster to read than JSON.
/// </summary>
/// <remarks>
/// The default options use the contractless resolver, so types need no attributes, and LZ4 compression.
/// Register it with <see cref="MessagePackServiceCollectionExtensions.AddMethodCacheMessagePack"/>.
/// </remarks>
public sealed class MessagePackCacheSerializer : ISerializer
{
    private readonly MessagePackSerializerOptions _options;

    /// <summary>
    /// Creates a serializer with the given <paramref name="options"/>, or contractless and LZ4-compressed ones.
    /// </summary>
    public MessagePackCacheSerializer(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithResolver(ContractlessStandardResolver.Instance);
    }

    /// <inheritdoc />
    public string ContentType => "application/x-msgpack";

    /// <inheritdoc />
    public void Serialize<T>(T value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        MessagePackSerializer.Serialize(destination, value, _options);
    }

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlySequence<byte> source)
        => source.IsEmpty ? default : MessagePackSerializer.Deserialize<T>(source, _options);
}
