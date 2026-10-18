using System.Buffers;
using MessagePack;
using MessagePack.Resolvers;
using MethodCache.Extensibility;

namespace MethodCache.Serialization.MessagePack;

/// <summary>
/// Writes cached values for the distributed cache with MessagePack: smaller and faster to read than JSON.
/// </summary>
/// <remarks>
/// The default options use the contractless resolver, so types need no attributes, keep <see cref="DateTime.Kind"/>,
/// use LZ4 compression and treat stored data as untrusted.
/// Register it with <see cref="Microsoft.Extensions.DependencyInjection.MethodCacheMessagePackServiceCollectionExtensions.AddMethodCacheMessagePack"/>.
/// </remarks>
internal sealed class MessagePackCacheSerializer : ISerializer
{
    private readonly MessagePackSerializerOptions _options;

    /// <summary>
    /// Creates a serializer with the given <paramref name="options"/>, or the defaults described above.
    /// </summary>
    public MessagePackCacheSerializer(MessagePackSerializerOptions? options = null)
    {
        _options = options ?? MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            // Native DateTime first: the standard format turns local and unspecified times into UTC, so a value
            // read from the distributed cache would differ from the one the method returned
            .WithResolver(CompositeResolver.Create(NativeDateTimeResolver.Instance, ContractlessStandardResolver.Instance))
            // Bounds what a crafted entry in the distributed cache can cost to read, such as hash collisions
            .WithSecurity(MessagePackSecurity.UntrustedData);
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
