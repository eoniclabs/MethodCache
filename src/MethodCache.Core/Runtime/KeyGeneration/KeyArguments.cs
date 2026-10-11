using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MethodCache.Core.Runtime.KeyGeneration;

/// <summary>
/// Serializes key arguments that have no built-in format, for the key generators that hash them.
/// </summary>
internal static class KeyArguments
{
    // Fields count too, so two values that differ only in a public field don't share a key. JSON writes every lone
    // surrogate as U+FFFD, so strings and chars that hold one are written in a form no valid text can take. A value
    // whose runtime type differs from the type it is declared as is written with that type (see RuntimeTypeResolver).
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default)
    {
        IncludeFields = true,
        Converters = { new LosslessStringConverter(), new LosslessCharConverter() },
        TypeInfoResolver = new RuntimeTypeResolver(),
    };

    private static readonly ConcurrentDictionary<Type, string> TypeNames = new();

    private static readonly ConcurrentDictionary<Type, bool> HasHiddenState = new();

    /// <summary>
    /// Serializes <paramref name="arg"/> to JSON. Throws when the type can't be serialized, or when all of its
    /// state is non-public: every value would then serialize to <c>{}</c> and share one key, and callers would get
    /// each other's cached results.
    /// </summary>
    public static byte[] Serialize(object arg)
    {
        var type = arg.GetType();
        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(arg, JsonOptions.GetTypeInfo(type));
        }
        catch (Exception ex)
        {
            // Falling back to ToString() would give different values the same key and return the wrong data.
            throw Unsupported(type, "it can't be serialized", ex);
        }

        if (json.Length == 2 && json[0] == (byte)'{' && json[1] == (byte)'}' &&
            HasHiddenState.GetOrAdd(type, static t => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0))
        {
            throw Unsupported(type, "its state is not public, so every value would get the same key", inner: null);
        }

        return json;
    }

    private static InvalidOperationException Unsupported(Type type, string reason, Exception? inner)
        => new(
            $"MethodCache cannot build a cache key from an argument of type '{type.FullName}': {reason}. " +
            $"Implement {nameof(ICacheKeyProvider)} on the type, or pass a type with public properties.", inner);

    /// <summary>
    /// The one property of the object a lone surrogate is written as. Dictionary keys starting with U+FFFF are escaped
    /// and no C# member name can hold it, so no other value writes this object.
    /// </summary>
    private const string MarkerName = "\uFFFF";

    /// <summary>
    /// The one property name that starts the object a value of another runtime type than its declared type is written
    /// as. Other property names starting with U+FFFF are an escaped name (U+FFFF twice) or a lone surrogate's (U+FFFF
    /// and hex digits), and a char key's name is one character, so no other value writes it.
    /// </summary>
    private const string TypeMarkerName = "\uFFFFt";

    private static readonly JsonEncodedText TypeMarker = JsonEncodedText.Encode(TypeMarkerName);

    private static readonly JsonEncodedText ValueName = JsonEncodedText.Encode("v");

    // By runtime type alone: RuntimeTypeConverter only runs with JsonOptions
    private static readonly ConcurrentDictionary<Type, RuntimeWriter> RuntimeWriters = new();

    /// <summary>
    /// The namespace-qualified name of <paramref name="type"/>, with its generic arguments but without the assembly
    /// versions <see cref="Type.FullName"/> puts in them, so the name stays the same across releases.
    /// </summary>
    public static string TypeName(Type type) => TypeNames.GetOrAdd(type, static t =>
    {
        var builder = new StringBuilder();
        AppendTypeName(builder, t);
        return builder.ToString();
    });

    private static void AppendTypeName(StringBuilder builder, Type type)
    {
        if (type.IsArray)
        {
            AppendTypeName(builder, type.GetElementType()!);
            builder.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
            return;
        }

        if (!type.IsGenericType)
        {
            builder.Append(type.FullName ?? type.Name);
            return;
        }

        var definition = type.GetGenericTypeDefinition();
        builder.Append(definition.FullName ?? definition.Name).Append('[');
        var arguments = type.GetGenericArguments();
        for (var i = 0; i < arguments.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendTypeName(builder, arguments[i]);
        }

        builder.Append(']');
    }

    /// <summary>
    /// True when every value declared as <paramref name="type"/> has exactly that runtime type: value types, and sealed
    /// classes (arrays only when their element type is exact, since an <c>object[]</c> can hold a <c>string[]</c>).
    /// </summary>
    private static bool IsExact(Type type)
        => type.IsValueType || (type.IsSealed && (!type.IsArray || IsExact(type.GetElementType()!)));

    /// <summary>
    /// Resolves the usual contracts, except for a type that can hold values of other runtime types (<c>object</c>,
    /// interfaces, unsealed classes): its values go through <see cref="RuntimeTypeConverter{T}"/>. Without it JSON
    /// writes <c>1</c> and <c>1L</c> in an <c>object[]</c> alike, and writes a value behind a base-typed property with
    /// the base type's members only, so values that differ in a derived type's members would share a key.
    /// </summary>
    private sealed class RuntimeTypeResolver : IJsonTypeInfoResolver
    {
        private static readonly MethodInfo CreateInfoMethod =
            typeof(RuntimeTypeResolver).GetMethod(nameof(CreateInfo), BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly DefaultJsonTypeInfoResolver _default = new();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
            => IsExact(type)
                ? _default.GetTypeInfo(type, options)
                : (JsonTypeInfo)CreateInfoMethod.MakeGenericMethod(type).Invoke(null, [_default, options])!;

        private static JsonTypeInfo<T> CreateInfo<T>(DefaultJsonTypeInfoResolver resolver, JsonSerializerOptions options)
            => JsonMetadataServices.CreateValueInfo<T>(options, new RuntimeTypeConverter<T>(resolver));
    }

    /// <summary>
    /// Writes a value of the declared type <typeparamref name="T"/> as usual, and a value of another runtime type as
    /// <c>{"\uFFFFt": "runtime type name", "v": the value written as its runtime type}</c>.
    /// </summary>
    private sealed class RuntimeTypeConverter<T>(DefaultJsonTypeInfoResolver resolver) : JsonConverter<T>
    {
        private JsonTypeInfo<T>? _exact;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var type = value!.GetType();
            if (type == typeof(T))
            {
                // The usual contract of T, built here because options resolve T to this converter
                _exact ??= (JsonTypeInfo<T>)resolver.GetTypeInfo(typeof(T), options);
                JsonSerializer.Serialize(writer, value, _exact);
                return;
            }

            var runtime = RuntimeWriters.GetOrAdd(type, static (t, o) => RuntimeWriter.Create(t, o), options);
            writer.WriteStartObject();
            writer.WriteString(TypeMarker, runtime.Name);
            writer.WritePropertyName(ValueName);
            runtime.Write(writer, value, options);
            writer.WriteEndObject();
        }
    }

    /// <summary>Writes a value as its runtime type, with that type's converter, and holds the type's name.</summary>
    private abstract class RuntimeWriter
    {
        private static readonly MethodInfo CreateMethod =
            typeof(RuntimeWriter).GetMethod(nameof(CreateTyped), BindingFlags.NonPublic | BindingFlags.Static)!;

        public JsonEncodedText Name { get; private init; }

        public static RuntimeWriter Create(Type type, JsonSerializerOptions options)
            => (RuntimeWriter)CreateMethod.MakeGenericMethod(type).Invoke(null, [options])!;

        public abstract void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options);

        private static RuntimeWriter CreateTyped<T>(JsonSerializerOptions options)
            => new Typed<T>((JsonConverter<T>)options.GetTypeInfo(typeof(T)).Converter)
            {
                Name = JsonEncodedText.Encode(TypeName(typeof(T))),
            };

        private sealed class Typed<T>(JsonConverter<T> converter) : RuntimeWriter
        {
            public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
                => converter.Write(writer, (T)value, options);
        }
    }

    /// <summary>True when <paramref name="value"/> has a surrogate that is not part of a pair.</summary>
    private static bool HasLoneSurrogate(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes a string with a lone surrogate as <c>{"\uFFFF": [its UTF-16 code units]}</c>, which no other value writes. As a
    /// property name, such a string and any name starting with U+FFFF get a U+FFFF prefix; the former then continues
    /// with its code units in hex, the latter with its own U+FFFF, so no two names meet.
    /// </summary>
    private sealed class LosslessStringConverter : JsonConverter<string>
    {
        private const char Marker = '\uFFFF';

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (!HasLoneSurrogate(value))
            {
                writer.WriteStringValue(value);
                return;
            }

            writer.WriteStartObject();
            writer.WriteStartArray(MarkerName);
            foreach (var c in value)
            {
                writer.WriteNumberValue(c);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (HasLoneSurrogate(value))
            {
                writer.WritePropertyName(Marker + string.Concat(value.Select(static c => ((int)c).ToString("x4", CultureInfo.InvariantCulture))));
            }
            else
            {
                writer.WritePropertyName(value.Length > 0 && value[0] == Marker ? Marker + value : value);
            }
        }
    }

    /// <summary>Writes a lone surrogate char as <c>{"\uFFFF": its number}</c>, which no other value writes.</summary>
    private sealed class LosslessCharConverter : JsonConverter<char>
    {
        public override char Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
        {
            if (char.IsSurrogate(value))
            {
                writer.WriteStartObject();
                writer.WriteNumber(MarkerName, value);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStringValue(value.ToString());
            }
        }

        public override void WriteAsPropertyName(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
            => writer.WritePropertyName(char.IsSurrogate(value) ? "\uFFFF" + ((int)value).ToString("x4", CultureInfo.InvariantCulture) : value.ToString());
    }
}
