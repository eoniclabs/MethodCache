using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MethodCache;

/// <summary>
/// Builds the tag for <see cref="MethodCacheKeys.Tag"/> from an interpolated string, writing each value the way the tag
/// templates of <c>[Cache]</c> write it. The compiler creates it; it isn't called directly.
/// </summary>
/// <remarks>
/// Only the types MethodCache writes in tags have an <c>AppendFormatted</c> overload, and none takes a format or an
/// alignment, so a value that would be written differently from the cached entries' tags doesn't compile.
/// </remarks>
[InterpolatedStringHandler]
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct TagInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _text;

    /// <summary>Starts a tag of about <paramref name="literalLength"/> characters with <paramref name="formattedCount"/> values.</summary>
    /// <param name="literalLength">The number of constant characters in the interpolated string.</param>
    /// <param name="formattedCount">The number of values in the interpolated string.</param>
    public TagInterpolatedStringHandler(int literalLength, int formattedCount)
        => _text = new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture);

    /// <summary>Writes constant text as it is.</summary>
    /// <param name="value">The text.</param>
    public void AppendLiteral(string value) => _text.AppendLiteral(value);

    /// <summary>Writes a string with <c>:</c> and <c>\</c> escaped, and <c>null</c> as <c>\0</c>.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(string? value) => _text.AppendLiteral(MethodCacheKeys.Part(value));

    /// <summary>Writes a character as a one-character string.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(char value) => _text.AppendLiteral(MethodCacheKeys.Part(value));

    /// <summary>Writes <c>true</c> or <c>false</c>.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(bool value) => _text.AppendLiteral(MethodCacheKeys.Part(value));

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(byte value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(sbyte value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(short value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(ushort value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(int value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(uint value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(long value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(ulong value) => _text.AppendFormatted(value);

    /// <summary>Writes the number in the invariant culture so that it round-trips.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(float value) => _text.AppendLiteral(MethodCacheKeys.Part(value));

    /// <summary>Writes the number in the invariant culture so that it round-trips.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(double value) => _text.AppendLiteral(MethodCacheKeys.Part(value));

    /// <summary>Writes the number in the invariant culture.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(decimal value) => _text.AppendFormatted(value);

    /// <summary>Writes 32 digits without hyphens.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(Guid value) => _text.AppendFormatted(value, "N");

    /// <summary>Writes ISO 8601 with full precision; a local time is converted to UTC first.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(DateTime value) => _text.AppendLiteral(MethodCacheKeys.Part(value));

    /// <summary>Writes ISO 8601 with full precision and the offset.</summary>
    /// <param name="value">The value.</param>
    public void AppendFormatted(DateTimeOffset value) => _text.AppendFormatted(value, "O");

    /// <summary>Writes the enum member's number, so renaming a member keeps existing tags.</summary>
    /// <typeparam name="TEnum">The enum type.</typeparam>
    /// <param name="value">The value.</param>
    public void AppendFormatted<TEnum>(TEnum value) where TEnum : struct, Enum => _text.AppendLiteral(MethodCacheKeys.Part(value));

    internal string ToStringAndClear() => _text.ToStringAndClear();
}
