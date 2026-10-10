using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MethodCache.Core.Storage.Abstractions;

namespace MethodCache.Core.Infrastructure.Serialization;

/// <summary>
/// Default serializer for values in the distributed cache: UTF-8 JSON written with System.Text.Json.
/// </summary>
/// <remarks>
/// Without options it uses <see cref="JsonSerializerOptions.Default"/>, which reads each type's shape once by
/// reflection. Under Native AOT, or to control the format, register one with options whose resolver includes your
/// <c>JsonSerializerContext</c>:
/// <code>
/// [JsonSerializable(typeof(User))]
/// internal partial class AppJsonContext : JsonSerializerContext { }
///
/// builder.Services.AddSingleton&lt;ISerializer&gt;(new JsonCacheSerializer(AppJsonContext.Default.Options));
/// </code>
/// For smaller values, the MethodCache.Serialization.MessagePack package stores them as MessagePack instead.
/// </remarks>
public sealed class JsonCacheSerializer : ISerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a serializer with the given <paramref name="options"/>, or <see cref="JsonSerializerOptions.Default"/>.
    /// </summary>
    public JsonCacheSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? JsonSerializerOptions.Default;
    }

    /// <inheritdoc />
    public string ContentType => "application/json";

    /// <inheritdoc />
    public void Serialize<T>(T value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var writer = new Utf8JsonWriter(destination);
        JsonSerializer.Serialize(writer, value, TypeInfo<T>());
    }

    /// <inheritdoc />
    public T? Deserialize<T>(ReadOnlySequence<byte> source)
    {
        if (source.IsEmpty)
        {
            return default;
        }

        var reader = new Utf8JsonReader(source);
        return JsonSerializer.Deserialize(ref reader, TypeInfo<T>());
    }

    // Looked up through the options so a JsonSerializerContext resolver works without reflection
    private JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
}
