using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
    private readonly ConcurrentDictionary<Type, Type?> _writtenEmpty = new();

    /// <inheritdoc />
    public IReadOnlyDictionary<Type, Type[]> Restored { get; } = new Dictionary<Type, Type[]>();

    /// <inheritdoc />
    public IReadOnlyList<(Type Declared, Type Derived)> Missing { get; } = [];

    /// <summary>
    /// Why the reflection here reports nothing: System.Text.Json reads types by reflection only for options without a
    /// resolver, and only while reflection is on. Native AOT turns it off, and the docs ask trimmed and Native AOT apps
    /// with a distributed cache to pass a JsonSerializerContext with UseJson(context).
    /// </summary>
    private const string ReflectionJustification =
        "Reflection-based JSON is used only for options without a resolver while reflection is enabled; trimmed and Native AOT apps pass a JsonSerializerContext with UseJson(context), as the docs say.";

    /// <summary>
    /// Creates a serializer with the given <paramref name="options"/>, or System.Text.Json's defaults with
    /// <see cref="JsonSerializerOptions.IncludeFields"/> on, that also writes and restores the derived types in
    /// <paramref name="derived"/> as themselves.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = ReflectionJustification)]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = ReflectionJustification)]
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

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = ReflectionJustification)]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = ReflectionJustification)]
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

    /// <summary>
    /// The first type in what a value of <paramref name="type"/> holds (the type itself, an element, a dictionary key
    /// or value, a property, or a derived type it restores) that these options write as an empty object although it
    /// keeps state in fields, such as a struct from a package that keeps it in private fields and has no converter in
    /// these options. It would read back empty, and writing that again gives the same bytes, so only its contract
    /// shows the loss. Null when there is none.
    /// </summary>
    internal Type? WrittenEmpty(Type type)
        => _writtenEmpty.GetOrAdd(type, static (declared, self) => self.FindWrittenEmpty(declared, []), this);

    private Type? FindWrittenEmpty(Type type, HashSet<Type> visited)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!visited.Add(type)) return null;

        JsonTypeInfo info;
        try
        {
            info = _options.GetTypeInfo(type);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // No contract here, so values of it fail to write and stay in memory anyway
            return null;
        }

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
                return info.ElementType is { } element ? FindWrittenEmpty(element, visited) : null;
            case JsonTypeInfoKind.Dictionary:
                return (info.KeyType is { } key ? FindWrittenEmpty(key, visited) : null) ??
                       (info.ElementType is { } value ? FindWrittenEmpty(value, visited) : null);
            case JsonTypeInfoKind.Object:
                if (info.Properties.Count == 0 && HoldsFields(type)) return type;
                foreach (var property in info.Properties)
                {
                    if (FindWrittenEmpty(property.PropertyType, visited) is { } found) return found;
                }

                foreach (var derived in info.PolymorphismOptions?.DerivedTypes ?? [])
                {
                    if (FindWrittenEmpty(derived.DerivedType, visited) is { } found) return found;
                }

                return null;
            default:
                // A converter writes it: a number, a string, a date, or a type with a converter of its own or in the options
                return null;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "A check only: fields trimming removed aren't counted, so it finds less, never a type that writes its state.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "A check only: fields trimming removed aren't counted, so it finds less, never a type that writes its state.")]
    private static bool HoldsFields(Type type)
    {
        for (var current = type; current != null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
        {
            if (current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Length > 0) return true;
        }

        return false;
    }
}
