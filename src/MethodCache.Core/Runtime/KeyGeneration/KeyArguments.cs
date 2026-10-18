using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MethodCache.Infrastructure;

namespace MethodCache.Core.Runtime.KeyGeneration;

/// <summary>
/// Serializes key arguments that have no built-in format, for the key generators that hash them.
/// </summary>
internal static class KeyArguments
{
    // Fields count too, so two values that differ only in a public field don't share a key. JSON writes every lone
    // surrogate as U+FFFD, so strings and chars that hold one are written in a form no valid text can take. A value
    // whose runtime type differs from the type it is declared as is written with that type (see RuntimeTypeResolver).
    // JSON has no converter for nint and nuint, so they are written as the 64-bit numbers they widen to. NaN and
    // infinities, such as a "no limit" double.PositiveInfinity, are written as their names instead of failing. Framework
    // types whose public properties don't hold their value (BigInteger's are IsZero, Sign...; IPAddress's throw) are
    // written as their text. A JsonElement is written as its JSON.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default)
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new LosslessStringConverter(), new LosslessCharConverter(), new NativeIntConverter(), new NativeUIntConverter(), new BigIntegerConverter(), new IPAddressConverter() },
        TypeInfoResolver = new RuntimeTypeResolver(),
    };

    /// <summary>
    /// Serializes <paramref name="arg"/> to JSON. Throws when the type can't be serialized, or when it or any value
    /// inside it (a property, an element) has a type whose state is all non-public: every such value would serialize
    /// to <c>{}</c>, different arguments would share one key, and callers would get each other's cached results.
    /// </summary>
    public static byte[] Serialize(object arg)
    {
        var type = KeyType(arg);
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(arg, JsonOptions.GetTypeInfo(type));
        }
        catch (HiddenStateException hidden)
        {
            throw Unsupported(type, hidden.Type == type
                ? "its state is not public, so every value would get the same key"
                : $"it holds a '{hidden.Type.FullName}', whose state is not public, so different values would get the same key",
                inner: null);
        }
        catch (Exception ex)
        {
            // Falling back to ToString() would give different values the same key and return the wrong data.
            throw Unsupported(type, "it can't be serialized", ex);
        }
    }

    /// <summary>
    /// Makes the contract of a type that JSON writes as <c>{}</c> but that holds instance state refuse to write.
    /// Contracts are built once per type, so serializing pays nothing for the check.
    /// </summary>
    private static JsonTypeInfo? RefuseHiddenState(JsonTypeInfo? info)
    {
        if (info is { Kind: JsonTypeInfoKind.Object, Properties.Count: 0 } && HasInstanceFields(info.Type))
        {
            var type = info.Type;
            info.OnSerializing = _ => throw new HiddenStateException(type);
        }

        return info;
    }

    /// <summary>Thrown while writing a value whose type has only non-public state.</summary>
    private sealed class HiddenStateException(Type type) : Exception
    {
        public Type Type { get; } = type;
    }

    private static bool HasInstanceFields(Type type)
    {
        // GetFields without DeclaredOnly omits private base fields. Inspect every level so inheritance cannot
        // turn stateful objects into the same empty JSON object.
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Length != 0)
            {
                return true;
            }
        }

        return false;
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

    private static readonly ConcurrentDictionary<Type, string> TypeNames = new();
    /// <summary>Version-independent type identity, including assemblies for nested runtime values and generic arguments.</summary>
    public static string TypeName(Type type) => TypeNames.GetOrAdd(type, static item => StableTypeIdentity.For(item));

    /// <summary>
    /// The type a value is written as: its runtime type, except that the framework's own read-only subclass of
    /// <see cref="System.Net.IPAddress"/> (behind <c>IPAddress.Loopback</c> and the like) is an <c>IPAddress</c>, so
    /// <c>IPAddress.Loopback</c> and <c>IPAddress.Parse("127.0.0.1")</c> get one key.
    /// </summary>
    internal static Type KeyType(object value) => value is System.Net.IPAddress ? typeof(System.Net.IPAddress) : value.GetType();

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
                ? ExactInfo(_default, type, options)
                : (JsonTypeInfo)CreateInfoMethod.MakeGenericMethod(type).Invoke(null, [_default, options])!;

        private static JsonTypeInfo<T> CreateInfo<T>(DefaultJsonTypeInfoResolver resolver, JsonSerializerOptions options)
            => JsonMetadataServices.CreateValueInfo<T>(options, new RuntimeTypeConverter<T>(resolver));
    }

    private static JsonTypeInfo? ExactInfo(DefaultJsonTypeInfoResolver resolver, Type type, JsonSerializerOptions options)
    {
        var info = RefuseHiddenState(resolver.GetTypeInfo(type, options));
        return KeyCollectionState.For(type).Length == 0 ? info
            : (JsonTypeInfo)CreateCollectionInfoMethod.MakeGenericMethod(type).Invoke(null, [info, options])!;
    }

    private static readonly MethodInfo CreateCollectionInfoMethod = typeof(KeyArguments)
        .GetMethod(nameof(CreateCollectionInfo), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static JsonTypeInfo<T> CreateCollectionInfo<T>(JsonTypeInfo<T> elements, JsonSerializerOptions options)
        => JsonMetadataServices.CreateValueInfo<T>(options, new CollectionStateConverter<T>(elements));

    private sealed class CollectionStateConverter<T>(JsonTypeInfo<T> elements) : JsonConverter<T>
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            // Every instance of this runtime type has this envelope; state never aliases an element list.
            writer.WriteStartObject();
            writer.WriteStartObject("state");
            foreach (var member in KeyCollectionState.For(typeof(T)))
            {
                writer.WritePropertyName(member.Name);
                JsonSerializer.Serialize(writer, member.Read(value!), options.GetTypeInfo(member.Type));
            }
            writer.WriteEndObject();
            writer.WritePropertyName("items");
            JsonSerializer.Serialize(writer, value, elements);
            writer.WriteEndObject();
        }
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
            var type = KeyType(value!);
            if (type == typeof(T))
            {
                // The usual contract of T, built here because options resolve T to this converter
                _exact ??= (JsonTypeInfo<T>)ExactInfo(resolver, typeof(T), options)!;
                JsonSerializer.Serialize(writer, value, _exact);
                return;
            }

            var runtime = RuntimeWriters.GetOrAdd(type, static (t, o) => RuntimeWriter.Create(t, o), options);
            writer.WriteStartObject();
            writer.WriteString(TypeMarker, runtime.Name);
            writer.WritePropertyName(ValueName);
            runtime.Write(writer, value!, options);
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

    /// <summary>
    /// Writes an <see cref="nint"/> as the <see cref="long"/> it widens to, so a value has the same key on 32-bit and
    /// 64-bit processes. In an <c>object</c> slot its runtime type is written too, so it stays apart from a <c>long</c>.
    /// </summary>
    private sealed class NativeIntConverter : JsonConverter<nint>
    {
        public override nint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, nint value, JsonSerializerOptions options)
            => writer.WriteNumberValue((long)value);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, nint value, JsonSerializerOptions options)
            => writer.WritePropertyName(((long)value).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes an <see cref="nuint"/> as the <see cref="ulong"/> it widens to, like <see cref="NativeIntConverter"/>.</summary>
    private sealed class NativeUIntConverter : JsonConverter<nuint>
    {
        public override nuint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, nuint value, JsonSerializerOptions options)
            => writer.WriteNumberValue((ulong)value);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, nuint value, JsonSerializerOptions options)
            => writer.WritePropertyName(((ulong)value).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes a <see cref="System.Numerics.BigInteger"/> as its digits.</summary>
    private sealed class BigIntegerConverter : JsonConverter<System.Numerics.BigInteger>
    {
        public override System.Numerics.BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, System.Numerics.BigInteger value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes an <see cref="System.Net.IPAddress"/> as its text, with the scope of an IPv6 address.</summary>
    private sealed class IPAddressConverter : JsonConverter<System.Net.IPAddress>
    {
        public override System.Net.IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, System.Net.IPAddress value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
