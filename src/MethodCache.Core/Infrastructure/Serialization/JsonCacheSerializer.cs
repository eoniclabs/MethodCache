using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace MethodCache.Extensibility;

/// <summary>
/// Default serializer for values in the distributed cache: UTF-8 JSON written with System.Text.Json.
/// </summary>
/// <remarks>
/// Without options it uses System.Text.Json's defaults with public fields included (so value tuples and structs
/// with fields keep their data), reading each type's shape once by reflection. Under Native AOT, or to control the format, register one with options whose resolver includes your
/// <c>JsonSerializerContext</c>:
/// <code>
/// [JsonSerializable(typeof(User))]
/// internal partial class AppJsonContext : JsonSerializerContext { }
///
/// builder.Services.AddMethodCache(cache =&gt; cache.UseJson(AppJsonContext.Default));
/// </code>
/// For smaller values, the MethodCache.Serialization.MessagePack package stores them as MessagePack instead.
/// </remarks>
internal sealed class JsonCacheSerializer : ISerializer
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Creates a serializer with the given <paramref name="options"/>, or System.Text.Json's defaults with
    /// <see cref="JsonSerializerOptions.IncludeFields"/> on.
    /// </summary>
    public JsonCacheSerializer(JsonSerializerOptions? options = null)
    {
        _options = options == null ? Defaults.Options : new JsonSerializerOptions(options);
        if (!_options.IsReadOnly) _options.MakeReadOnly(populateMissingResolver: true);
    }

    /// <summary>Created on first use, so a serializer given its own options never builds these.</summary>
    private static class Defaults
    {
        public static readonly JsonSerializerOptions Options = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions(JsonSerializerOptions.Default) { IncludeFields = true };
            options.MakeReadOnly();
            return options;
        }
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
