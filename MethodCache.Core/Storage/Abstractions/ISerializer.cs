namespace MethodCache.Core.Storage.Abstractions;

/// <summary>
/// Defines serialization operations for storage providers.
/// </summary>
public interface ISerializer
{
    /// <summary>
    /// Serializes an object to bytes.
    /// </summary>
    byte[] Serialize<T>(T obj);

    /// <summary>
    /// Deserializes bytes to an object.
    /// </summary>
    T? Deserialize<T>(byte[] data);

    /// <summary>
    /// Deserializes bytes to an object.
    /// </summary>
    T? Deserialize<T>(ReadOnlySpan<byte> data);

    /// <summary>
    /// Gets the content type identifier for this serializer.
    /// </summary>
    string ContentType { get; }
}