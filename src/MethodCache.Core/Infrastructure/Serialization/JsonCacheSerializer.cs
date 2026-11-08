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
internal sealed class JsonCacheSerializer : ISerializer, MethodCache.Infrastructure.IRestoresDerivedTypes
{
    private readonly JsonSerializerOptions _options;

    /// <inheritdoc />
    public IReadOnlyDictionary<Type, Type[]> Restored { get; } = new Dictionary<Type, Type[]>();

    /// <inheritdoc />
    public IReadOnlyList<(Type Declared, Type Derived)> Missing { get; } = [];

    /// <summary>
    /// Creates a serializer with the given <paramref name="options"/>, or System.Text.Json's defaults with
    /// <see cref="JsonSerializerOptions.IncludeFields"/> on, that also writes and restores the derived types in
    /// <paramref name="derived"/> as themselves.
    /// </summary>
    public JsonCacheSerializer(JsonSerializerOptions? options = null, MethodCache.Infrastructure.DerivedTypeRegistry? derived = null)
    {
        if (derived is not { Count: > 0 })
        {
            _options = options == null ? Defaults.Options : new JsonSerializerOptions(options);
        }
        else
        {
            _options = new JsonSerializerOptions(options ?? Defaults.Options);

            // Options without a resolver read the types by reflection, as MakeReadOnly would set up; under Native AOT
            // with reflection off there is no contract to add them to, and the check before sharing decides
            if (_options.TypeInfoResolver == null && JsonSerializer.IsReflectionEnabledByDefault) _options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
            if (_options.TypeInfoResolver is { } resolver)
            {
                (Restored, Missing) = Resolvable(resolver, _options, derived.Snapshot());
                if (Restored.Count > 0) _options.TypeInfoResolver = resolver.WithAddedModifier(MethodCache.Infrastructure.DerivedTypeRegistry.Modifier(Restored));
            }
        }

        if (!_options.IsReadOnly) _options.MakeReadOnly(populateMissingResolver: true);
    }

    /// <summary>
    /// The derived types the resolver has a contract for, such as those a <c>JsonSerializerContext</c> lists, of the
    /// declared types it has one for. A derived type without one would make every value of its declared type fail to
    /// write, so it is left out and named in the startup log instead.
    /// </summary>
    private static (Dictionary<Type, Type[]> Restored, List<(Type Declared, Type Derived)> Missing) Resolvable(
        IJsonTypeInfoResolver resolver, JsonSerializerOptions options, Dictionary<Type, Type[]> derived)
    {
        var restored = new Dictionary<Type, Type[]>();
        var missing = new List<(Type Declared, Type Derived)>();
        foreach (var (declared, types) in derived)
        {
            // A declared type without a contract is never written by this serializer, so neither are its derived types
            if (!Resolves(declared)) continue;
            var resolvable = types.Where(Resolves).ToArray();
            missing.AddRange(types.Except(resolvable).Select(type => (declared, type)));
            if (resolvable.Length > 0) restored.Add(declared, resolvable);
        }

        return (restored, missing);

        bool Resolves(Type type)
        {
            try
            {
                return resolver.GetTypeInfo(type, options) != null;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }
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
