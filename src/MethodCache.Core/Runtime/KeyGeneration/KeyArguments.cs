using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MethodCache.Core.Runtime.KeyGeneration;

/// <summary>
/// Serializes key arguments that have no built-in format, for the key generators that hash them.
/// </summary>
internal static class KeyArguments
{
    // Fields count too, so two values that differ only in a public field don't share a key. JSON writes every lone
    // surrogate as U+FFFD, so strings and chars that hold one are written in a form no valid text can take.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default)
    {
        IncludeFields = true,
        Converters = { new LosslessStringConverter(), new LosslessCharConverter() },
    };

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
    /// Writes a string with a lone surrogate as an array of its UTF-16 code units, which no string value equals. As a
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

            writer.WriteStartArray();
            foreach (var c in value)
            {
                writer.WriteNumberValue(c);
            }

            writer.WriteEndArray();
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

    /// <summary>Writes a lone surrogate char as its number, which no one-char string equals.</summary>
    private sealed class LosslessCharConverter : JsonConverter<char>
    {
        public override char Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
        {
            if (char.IsSurrogate(value))
            {
                writer.WriteNumberValue(value);
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
