using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MethodCache.Core.Runtime.KeyGeneration;

/// <summary>
/// Serializes key arguments that have no built-in format, for the key generators that hash them.
/// </summary>
internal static class KeyArguments
{
    // Fields count too, so two values that differ only in a public field don't share a key.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Default) { IncludeFields = true };

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
}
