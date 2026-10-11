using System.Buffers;

namespace MethodCache.Extensibility;

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
    /// Identifies the format, for example <c>application/json</c>. It is stored with each entry, and entries written
    /// with another content type are read as misses, so switching serializers needs no cache flush.
    /// </summary>
    string ContentType { get; }
}
