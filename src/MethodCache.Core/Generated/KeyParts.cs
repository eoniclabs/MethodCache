using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MethodCache.Generated;

/// <summary>Stable argument formatting helpers for generated keys.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class KeyParts
{
    /// <summary>Formats a dynamic tag argument using the canonical scalar key-part rules.</summary>
    public static string TagPart(object? value) => Infrastructure.CanonicalTagPart.Format(value);

    /// <summary>The encoded null argument.</summary>
    public const string Null = "\\0";
    /// <summary>The key argument separator.</summary>
    public const string Separator = ":";
    /// <summary>Reads and escapes the public key provider's part.</summary>
    public static string Provider(ICacheKeyProvider value) => Part(value.CacheKeyPart);
    /// <summary>Formats a string argument, escaping characters that would make keys ambiguous.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(string? value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="bool"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(bool value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="char"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(char value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="byte"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(byte value) => MethodCacheKeys.Part(value);

    /// <summary>Formats an <see cref="sbyte"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(sbyte value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="short"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(short value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="ushort"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(ushort value) => MethodCacheKeys.Part(value);

    /// <summary>Formats an <see cref="int"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(int value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="uint"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(uint value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="long"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(long value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="ulong"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(ulong value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="float"/> argument so that it round-trips.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(float value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="double"/> argument so that it round-trips.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(double value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="decimal"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(decimal value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="Guid"/> argument.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(Guid value) => MethodCacheKeys.Part(value);

    /// <summary>
    /// Formats a <see cref="DateTime"/> argument with full precision. Local times are converted to UTC so that
    /// servers in different time zones produce the same key for the same instant.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(DateTime value) => MethodCacheKeys.Part(value);

    /// <summary>Formats a <see cref="DateTimeOffset"/> argument with full precision and its offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part(DateTimeOffset value) => MethodCacheKeys.Part(value);

    /// <summary>
    /// Formats a collection argument as <c>[{count}:{element}:{element}...]</c>, and <c>null</c> as <see cref="Null"/>.
    /// <paramref name="part"/> formats each element, and its text is escaped like a string part, so an element can't
    /// read as two; the count keeps the last element apart from the next argument. No two argument lists share a key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Sequence<T>(IEnumerable<T>? values, Func<T, string> part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (values is not T[] array) return MethodCacheKeys.Sequence(values, part);
        if (array.Length == 0) return "[0]";
        // Format each element once, then write the final text directly instead of copying a builder's buffer.
        var count = array.Length.ToString(CultureInfo.InvariantCulture);
        var parts = new string[array.Length];
        var length = checked(count.Length + 2 + array.Length);
        for (var index = 0; index < array.Length; index++)
        {
            parts[index] = MethodCacheKeys.Part(part(array[index]));
            length = checked(length + parts[index].Length);
        }
        return string.Create(length, (count, parts), static (destination, state) =>
        {
            destination[0] = '[';
            state.count.AsSpan().CopyTo(destination[1..]);
            var offset = state.count.Length + 1;
            foreach (var text in state.parts)
            {
                destination[offset++] = ':';
                text.AsSpan().CopyTo(destination[offset..]);
                offset += text.Length;
            }
            destination[offset] = ']';
        });
    }

    /// <summary>Formats an enum argument by its numeric value, so renaming a member keeps existing keys.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Part<TEnum>(TEnum value) where TEnum : struct, Enum => MethodCacheKeys.Part(value);
}
