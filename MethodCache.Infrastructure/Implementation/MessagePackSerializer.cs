using MessagePack;
using MethodCache.Infrastructure.Abstractions;

namespace MethodCache.Infrastructure.Implementation;

/// <summary>
/// MessagePack implementation of ISerializer.
/// </summary>
public class MessagePackSerializer : ISerializer
{
    private readonly MessagePackSerializerOptions _options;

    public MessagePackSerializer()
    {
        _options = MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance);
    }

    public MessagePackSerializer(MessagePackSerializerOptions options)
    {
        _options = options;
    }

    public string ContentType => "application/x-msgpack";

    public byte[] Serialize<T>(T obj)
    {
        try
        {
            return MessagePack.MessagePackSerializer.Serialize(obj, _options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to serialize object of type {typeof(T).Name}", ex);
        }
    }

    public T? Deserialize<T>(byte[] data)
    {
        if (data == null || data.Length == 0)
            return default;

        try
        {
            return MessagePack.MessagePackSerializer.Deserialize<T>(data, _options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to deserialize data to type {typeof(T).Name}", ex);
        }
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return default;

        try
        {
            // Convert span to array for MessagePack compatibility
            return MessagePack.MessagePackSerializer.Deserialize<T>(data.ToArray(), _options);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to deserialize data to type {typeof(T).Name}", ex);
        }
    }
}