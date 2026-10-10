using System.Buffers;

namespace MethodCache.Core.Storage.Abstractions;

/// <summary>
/// Turns cached values into bytes for the distributed cache and back.
/// </summary>
public interface ISerializer
{
    /// <summary>
    /// Writes <paramref name="value"/> to <paramref name="destination"/>.
    /// </summary>
    void Serialize<T>(T value, IBufferWriter<byte> destination);

    /// <summary>
    /// Reads a value written by <see cref="Serialize{T}(T, IBufferWriter{byte})"/>.
    /// </summary>
    T? Deserialize<T>(ReadOnlySequence<byte> source);

    /// <summary>
    /// Gets the content type identifier for this serializer.
    /// </summary>
    string ContentType { get; }
}
