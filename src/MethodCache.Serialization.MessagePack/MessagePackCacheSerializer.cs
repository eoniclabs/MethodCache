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
/// Register it with <c>UseMessagePack()</c> inside <c>AddMethodCache(mc =&gt; ...)</c>.
/// </remarks>
internal sealed class MessagePackCacheSerializer : ISerializer, MethodCache.Infrastructure.IRestoresDerivedTypes
{
    private readonly MessagePackSerializerOptions _options;

    /// <inheritdoc />
    public IReadOnlyDictionary<Type, Type[]> Restored { get; } = new Dictionary<Type, Type[]>();

    /// <inheritdoc />
    public IReadOnlyList<(Type Declared, Type Derived)> Missing { get; } = [];

    /// <summary>
    /// The defaults described above. <c>UseMessagePack(configure)</c> passes them to its callback, so changing one
    /// setting keeps the others.
    /// </summary>
    internal static MessagePackSerializerOptions DefaultOptions { get; } = MessagePackSerializerOptions.Standard
        .WithCompression(MessagePackCompression.Lz4BlockArray)
        // Native DateTime first: the standard format turns local and unspecified times into UTC, so a value
        // read from the distributed cache would differ from the one the method returned
        .WithResolver(CompositeResolver.Create(NativeDateTimeResolver.Instance, ContractlessStandardResolver.Instance))
        // Bounds what a crafted entry in the distributed cache can cost to read, such as hash collisions
        .WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>
    /// Creates a serializer with the given <paramref name="options"/>, or the defaults described above, that also
    /// writes and restores the derived types in <paramref name="derived"/> as themselves.
    /// </summary>
    public MessagePackCacheSerializer(MessagePackSerializerOptions? options = null, MethodCache.Infrastructure.DerivedTypeRegistry? derived = null)
    {
        _options = options ?? DefaultOptions;
        if (derived is { Count: > 0 })
        {
            var snapshot = derived.Snapshot();
            Restored = snapshot;
            _options = _options.WithResolver(new DerivedTypeResolver(_options.Resolver, snapshot));
        }
    }

    internal MessagePackSerializerOptions Options => _options;

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
