using MethodCache.Core.Runtime.Keys;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MethodCache.Generated;

/// <summary>
/// Writes a cache key into a caller-supplied buffer, usually on the stack, so a cache hit can be looked up without
/// allocating the key string. Produces exactly the text <see cref="MethodCacheKeys"/> builds. Generated decorators
/// use it; application code rarely needs it.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public ref struct KeyBuilder
{
    /// <summary>A buffer size, in characters, that fits typical keys without growing.</summary>
    internal const int StackBufferSize = 256;

    internal CallSnapshot Binding { get; }
    private string? _text;
    internal readonly string? StoredText => _text;

    private Span<char> _chars;
    private int _length;
    private int _prefixLength;

    /// <summary>
    /// Starts an inline key in the caller's buffer, bound to <paramref name="call"/>'s snapshot and starting with its
    /// prefix. A constructor rather than a factory method: a builder returned from a method is copied into the
    /// caller's local, which on x64 stalls the hit path (about 12 ns per hit).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public KeyBuilder(MethodCall call, Span<char> buffer)
    {
        var binding = call.Snapshot;
        _chars = buffer;
        _length = 0;
        _text = buffer.IsEmpty ? binding.Prefix : null;
        Binding = binding;
        _prefixLength = 0;
        if (!buffer.IsEmpty)
        {
            Append(binding.Prefix.AsSpan());
            _prefixLength = _length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal KeyBuilder(Span<char> initialBuffer, CallSnapshot binding)
    {
        _chars = initialBuffer;
        _length = 0;
        _prefixLength = 0;
        _text = null;
        Binding = binding;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal KeyBuilder(string text, CallSnapshot binding)
    {
        _chars = default;
        _length = 0;
        _prefixLength = 0;
        _text = text;
        Binding = binding;
    }

    // Always hash the current suffix. An Append after a lookup cannot reuse a stale final hash.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int LookupHash()
    {
        if (_prefixLength == 0) return Binding.HashKey(AsSpan());
        return Binding.PrefixHash.Finish(AsSpan()[_prefixLength..]);
    }

    /// <summary>The key written so far. Valid until the builder is appended to again.</summary>
    public readonly ReadOnlySpan<char> AsSpan() => _text is { } text ? text.AsSpan() : _chars[.._length];

    /// <summary>Returns the key as a string.</summary>
    public override readonly string ToString()
    {
        if (_text is { } owned) return owned;
        var text = new string(_chars[.._length]);
        if (Binding?.Services.FastMemory != null) ProcessKeyHash.Remember(text, Binding.HashKey(text));
        return text;
    }

    /// <summary>Appends text as is, such as the key prefix of a cached method.</summary>
    internal void Append(string value)
    {
        EnsureWritable();
        Append(value.AsSpan());
    }

    /// <summary>Appends one complex argument using the generator's canonical part formatter.</summary>
    /// <param name="value">The argument to format, including null when its declared type allows it.</param>
    /// <param name="format">Formats one complete canonical record, collection or provider part.</param>
    public void AppendPart<T>(T value, Func<T, string> format)
    {
        ArgumentNullException.ThrowIfNull(format);
        EnsureWritable();
        AppendSeparator();
        Append(format(value).AsSpan());
    }

    /// <summary>Appends a separator and a string argument, escaped as <see cref="MethodCacheKeys.Part(string)"/> does.</summary>
    public void AppendPart(string? value)
    {
        EnsureWritable();
        AppendSeparator();
        if (value is null)
        {
            Append(KeyParts.Null.AsSpan());
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
        EnsureWritable();
        AppendSeparator();
        Append(KeyParts.Null.AsSpan());
    }

    /// <summary>Appends a separator and a <see cref="bool"/> argument.</summary>
    public void AppendPart(bool value)
    {
        EnsureWritable();
        AppendSeparator();
        Append(value ? "true".AsSpan() : "false".AsSpan());
    }

    /// <summary>Appends a separator and a <see cref="char"/> argument.</summary>
    public void AppendPart(char value)
    {
        EnsureWritable();
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
        EnsureWritable();
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
        EnsureWritable();
        AppendSeparator();
        int written;
        while (!value.TryFormat(_chars[_length..], out written, format, CultureInfo.InvariantCulture))
        {
            Grow(1);
        }

        _length += written;
    }

    private void EnsureWritable()
    {
        if (_text is not { } text) return;
        _chars = new char[text.Length + 32];
        text.AsSpan().CopyTo(_chars);
        _length = text.Length;
        _text = null;
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
