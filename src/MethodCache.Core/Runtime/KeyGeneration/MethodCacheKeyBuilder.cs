using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MethodCache.Infrastructure;

/// <summary>
/// Writes a cache key into a caller-supplied buffer, usually on the stack, so a cache hit can be looked up without
/// allocating the key string. Produces exactly the text <see cref="MethodCacheKeys"/> builds. Generated decorators
/// use it; application code rarely needs it.
/// </summary>
/// <example>
/// <code>
/// var key = new MethodCacheKeyBuilder(stackalloc char[MethodCacheKeyBuilder.StackBufferSize]);
/// key.Append(prefix);
/// key.AppendPart(42);
/// if (cacheManager.TryGetFast&lt;User&gt;(key.AsSpan(), out var user)) { ... }
/// </code>
/// </example>
[EditorBrowsable(EditorBrowsableState.Never)]
internal ref struct MethodCacheKeyBuilder
{
    /// <summary>A buffer size, in characters, that fits typical keys without growing.</summary>
    public const int StackBufferSize = 256;

    private Span<char> _chars;
    private int _length;

    /// <summary>Creates a builder that writes into <paramref name="initialBuffer"/> and grows onto the heap when it is full.</summary>
    public MethodCacheKeyBuilder(Span<char> initialBuffer)
    {
        _chars = initialBuffer;
        _length = 0;
    }

    /// <summary>The key written so far. Valid until the builder is appended to again.</summary>
    public readonly ReadOnlySpan<char> AsSpan() => _chars[.._length];

    /// <summary>Returns the key as a string.</summary>
    public override readonly string ToString() => new(_chars[.._length]);

    /// <summary>Appends text as is, such as a key prefix from <see cref="MethodCacheKeys.Prefix"/>.</summary>
    public void Append(string value) => Append(value.AsSpan());

    /// <summary>Appends a separator and a string argument, escaped as <see cref="MethodCacheKeys.Part(string)"/> does.</summary>
    public void AppendPart(string? value)
    {
        AppendSeparator();
        if (value is null)
        {
            Append(MethodCacheKeys.Null.AsSpan());
            return;
        }

        var text = value.AsSpan();
        int special;
        while ((special = text.IndexOfAny('\\', ':')) >= 0)
        {
            Append(text[..special]);
            Append('\\');
            Append(text[special]);
            text = text[(special + 1)..];
        }

        Append(text);
    }

    /// <summary>Appends a separator and the part written for a <c>null</c> argument.</summary>
    public void AppendNullPart()
    {
        AppendSeparator();
        Append(MethodCacheKeys.Null.AsSpan());
    }

    /// <summary>Appends a separator and a <see cref="bool"/> argument.</summary>
    public void AppendPart(bool value)
    {
        AppendSeparator();
        Append(value ? "true".AsSpan() : "false".AsSpan());
    }

    /// <summary>Appends a separator and a <see cref="char"/> argument.</summary>
    public void AppendPart(char value)
    {
        AppendSeparator();
        if (value is '\\' or ':')
        {
            Append('\\');
        }

        Append(value);
    }

    /// <summary>Appends a separator and a <see cref="byte"/> argument.</summary>
    public void AppendPart(byte value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and an <see cref="sbyte"/> argument.</summary>
    public void AppendPart(sbyte value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and a <see cref="short"/> argument.</summary>
    public void AppendPart(short value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and a <see cref="ushort"/> argument.</summary>
    public void AppendPart(ushort value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and an <see cref="int"/> argument.</summary>
    public void AppendPart(int value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and a <see cref="uint"/> argument.</summary>
    public void AppendPart(uint value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and a <see cref="long"/> argument.</summary>
    public void AppendPart(long value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and a <see cref="ulong"/> argument.</summary>
    public void AppendPart(ulong value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and a <see cref="float"/> argument so that it round-trips.</summary>
    public void AppendPart(float value) => AppendFormatted(value, "R");

    /// <summary>Appends a separator and a <see cref="double"/> argument so that it round-trips.</summary>
    public void AppendPart(double value) => AppendFormatted(value, "R");

    /// <summary>Appends a separator and a <see cref="decimal"/> argument.</summary>
    public void AppendPart(decimal value) => AppendFormatted(value, default);

    /// <summary>Appends a separator and a <see cref="Guid"/> argument.</summary>
    public void AppendPart(Guid value) => AppendFormatted(value, "N");

    /// <summary>Appends a separator and a <see cref="DateTime"/> argument, converting local times to UTC.</summary>
    public void AppendPart(DateTime value)
        => AppendFormatted(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, "O");

    /// <summary>Appends a separator and a <see cref="DateTimeOffset"/> argument.</summary>
    public void AppendPart(DateTimeOffset value) => AppendFormatted(value, "O");

    /// <summary>Appends a separator and an enum argument by its numeric value.</summary>
    public void AppendPart<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        // Not Enum.TryFormat: it writes a negative value with the current culture's minus sign (U+2212 in sv-SE)
        switch (Type.GetTypeCode(typeof(TEnum)))
        {
            case TypeCode.SByte:
                AppendFormatted(Unsafe.As<TEnum, sbyte>(ref value), default);
                return;
            case TypeCode.Int16:
                AppendFormatted(Unsafe.As<TEnum, short>(ref value), default);
                return;
            case TypeCode.Int32:
                AppendFormatted(Unsafe.As<TEnum, int>(ref value), default);
                return;
            case TypeCode.Int64:
                AppendFormatted(Unsafe.As<TEnum, long>(ref value), default);
                return;
        }

        // Unsigned: no sign to format
        AppendSeparator();
        int written;
        while (!Enum.TryFormat(value, _chars[_length..], out written, "D"))
        {
            Grow(1);
        }

        _length += written;
    }

    private void AppendFormatted<T>(T value, ReadOnlySpan<char> format) where T : ISpanFormattable
    {
        AppendSeparator();
        int written;
        while (!value.TryFormat(_chars[_length..], out written, format, CultureInfo.InvariantCulture))
        {
            Grow(1);
        }

        _length += written;
    }

    private void AppendSeparator() => Append(':');

    private void Append(char value)
    {
        if (_length == _chars.Length)
        {
            Grow(1);
        }

        _chars[_length++] = value;
    }

    private void Append(ReadOnlySpan<char> value)
    {
        if (value.Length > _chars.Length - _length)
        {
            Grow(value.Length);
        }

        value.CopyTo(_chars[_length..]);
        _length += value.Length;
    }

    private void Grow(int additional)
    {
        // Rare: keys longer than the stack buffer pay one allocation, as the string key always did
        var grown = new char[Math.Max(_chars.Length * 2, _length + additional + 16)];
        _chars[.._length].CopyTo(grown);
        _chars = grown;
    }
}
