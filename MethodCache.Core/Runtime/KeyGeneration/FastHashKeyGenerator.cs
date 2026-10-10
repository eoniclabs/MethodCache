using System.Buffers;
using System.Buffers.Text;
using System.IO.Hashing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MethodCache.Core.Runtime.Core;

namespace MethodCache.Core.Runtime.KeyGeneration;

/// <summary>
/// Default cache key generator. Writes the method name, version and arguments into a canonical binary form and
/// hashes it with 128-bit XxHash3.
/// </summary>
/// <remarks>
/// Keys have the form <c>{methodName}[:v{version}]:{hash}</c>, so all entries of one method share a readable
/// prefix that pattern invalidation can match (see <see cref="MethodCacheKeys"/>). Arguments of other types than
/// the built-in primitives are serialized with System.Text.Json, public fields included; implement
/// <see cref="ICacheKeyProvider"/> on types that cannot be serialized, or under Native AOT.
/// </remarks>
/// <example>
/// <code>
/// // FastHashKeyGenerator is the default; register it explicitly to replace another generator
/// services.Replace(ServiceDescriptor.Singleton&lt;ICacheKeyGenerator, FastHashKeyGenerator&gt;());
/// </code>
/// </example>
public class FastHashKeyGenerator : ICacheKeyGenerator
{
    // Fields count too, so two values that differ only in a public field don't share a key.
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerOptions.Default) { IncludeFields = true };

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_writer;

    // Hex lookup table for fast conversion
    private static ReadOnlySpan<byte> HexChars => "0123456789abcdef"u8;

    public string GenerateKey(string methodName, object[] args, CacheRuntimePolicy policy)
        => GenerateKey(methodName, args, policy.Version);

    private static string GenerateKey(string methodName, object[] args, int? version)
    {
        var writer = t_writer ??= new ArrayBufferWriter<byte>(256);
        writer.ResetWrittenCount();

        WriteUtf8String(ref writer, methodName);

        if (version.HasValue)
        {
            WriteUtf8Literal(ref writer, "_v"u8);
            WriteInt32(ref writer, version.Value);
        }

        foreach (var arg in args)
        {
            WriteArgument(ref writer, arg);
        }

        var hash = XxHash128.HashToUInt128(writer.WrittenSpan);
        if (writer.Capacity > 64 * 1024)
        {
            // Don't keep a buffer grown by one huge argument alive on this thread.
            t_writer = null;
        }

        var prefix = version.HasValue
            ? string.Concat(methodName, ":v", version.Value.ToString(CultureInfo.InvariantCulture))
            : methodName;
        return string.Concat(prefix, ":", ToHexString(hash));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteArgument(ref ArrayBufferWriter<byte> writer, object? arg)
    {
        if (arg is null)
        {
            WriteUtf8Literal(ref writer, "_NULL"u8);
            return;
        }

        switch (arg)
        {
            case ICacheKeyProvider keyProvider:
                WriteUtf8String(ref writer, "_" + keyProvider.CacheKeyPart);
                break;
            case string s:
                WriteString(ref writer, s);
                break;
            case int i:
                WriteInt32WithPrefix(ref writer, i, "_INT:"u8);
                break;
            case long l:
                WriteInt64WithPrefix(ref writer, l, "_LONG:"u8);
                break;
            case bool b:
                WriteUtf8Literal(ref writer, b ? "_BOOL:True"u8 : "_BOOL:False"u8);
                break;
            case double d:
                WriteDouble(ref writer, d);
                break;
            case float f:
                WriteFloat(ref writer, f);
                break;
            case decimal dec:
                WriteDecimal(ref writer, dec);
                break;
            case DateTime dt:
                WriteDateTime(ref writer, dt);
                break;
            case DateTimeOffset dto:
                WriteDateTimeOffset(ref writer, dto);
                break;
            case Guid guid:
                WriteGuid(ref writer, guid);
                break;
            case Enum enumValue:
                WriteEnum(ref writer, enumValue);
                break;
            default:
                WriteComplexObject(ref writer, arg);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUtf8Literal(ref ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> literal)
    {
        var span = writer.GetSpan(literal.Length);
        literal.CopyTo(span);
        writer.Advance(literal.Length);
    }

    private static void WriteUtf8String(ref ArrayBufferWriter<byte> writer, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

#if NETSTANDARD2_0
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var span = writer.GetSpan(byteCount);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, 0, value.Length, rented, 0);
            rented.AsSpan(0, written).CopyTo(span);
            writer.Advance(written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
#else
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var span = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value.AsSpan(), span);
        writer.Advance(written);
#endif
    }

    private static void WriteString(ref ArrayBufferWriter<byte> writer, string value)
    {
        WriteUtf8Literal(ref writer, "_STR:"u8);
        var escaped = value.Replace("_", "__");
        WriteUtf8String(ref writer, escaped);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt32WithPrefix(ref ArrayBufferWriter<byte> writer, int value, ReadOnlySpan<byte> prefix)
    {
        WriteUtf8Literal(ref writer, prefix);
        WriteInt32(ref writer, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt64WithPrefix(ref ArrayBufferWriter<byte> writer, long value, ReadOnlySpan<byte> prefix)
    {
        WriteUtf8Literal(ref writer, prefix);
        WriteInt64(ref writer, value);
    }

    private static void WriteInt32(ref ArrayBufferWriter<byte> writer, int value)
    {
        var span = writer.GetSpan(16);
        if (!Utf8Formatter.TryFormat(value, span, out var written))
        {
            throw new InvalidOperationException("Failed to format int32 value.");
        }

        writer.Advance(written);
    }

    private static void WriteInt64(ref ArrayBufferWriter<byte> writer, long value)
    {
        var span = writer.GetSpan(24);
        if (!Utf8Formatter.TryFormat(value, span, out var written))
        {
            throw new InvalidOperationException("Failed to format int64 value.");
        }

        writer.Advance(written);
    }

    private static void WriteDouble(ref ArrayBufferWriter<byte> writer, double value)
    {
        WriteUtf8Literal(ref writer, "_DBL:"u8);
        var span = writer.GetSpan(32);
        if (!Utf8Formatter.TryFormat(value, span, out var written, new StandardFormat('G', 17)))
        {
            throw new InvalidOperationException("Failed to format double value.");
        }

        writer.Advance(written);
    }

    private static void WriteFloat(ref ArrayBufferWriter<byte> writer, float value)
    {
        WriteUtf8Literal(ref writer, "_FLT:"u8);
        var span = writer.GetSpan(24);
        if (!Utf8Formatter.TryFormat(value, span, out var written, new StandardFormat('G', 9)))
        {
            throw new InvalidOperationException("Failed to format float value.");
        }

        writer.Advance(written);
    }

    private static void WriteDecimal(ref ArrayBufferWriter<byte> writer, decimal value)
    {
        WriteUtf8Literal(ref writer, "_DEC:"u8);
        var span = writer.GetSpan(64);
        if (!Utf8Formatter.TryFormat(value, span, out var written))
        {
            throw new InvalidOperationException("Failed to format decimal value.");
        }

        writer.Advance(written);
    }

    private static void WriteDateTime(ref ArrayBufferWriter<byte> writer, DateTime value)
    {
        // ToBinary() would embed the server's local offset; normalize so every server agrees.
        WriteUtf8Literal(ref writer, "_DT:"u8);
        var normalized = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        WriteInt64(ref writer, normalized.Ticks);
        WriteUtf8Literal(ref writer, ":"u8);
        WriteInt32(ref writer, (int)normalized.Kind);
    }

    private static void WriteDateTimeOffset(ref ArrayBufferWriter<byte> writer, DateTimeOffset value)
    {
        WriteUtf8Literal(ref writer, "_DTO:"u8);
        WriteInt64(ref writer, value.ToUnixTimeMilliseconds());
        WriteUtf8Literal(ref writer, ":"u8);
        WriteUtf8String(ref writer, value.Offset.TotalMinutes.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteGuid(ref ArrayBufferWriter<byte> writer, Guid value)
    {
        WriteUtf8Literal(ref writer, "_GUID:"u8);
        var span = writer.GetSpan(32);
        if (!Utf8Formatter.TryFormat(value, span, out var written, new StandardFormat('N')))
        {
            throw new InvalidOperationException("Failed to format guid value.");
        }

        writer.Advance(written);
    }

    private static void WriteEnum(ref ArrayBufferWriter<byte> writer, Enum value)
    {
        WriteUtf8Literal(ref writer, "_ENUM:"u8);
        WriteUtf8String(ref writer, value.GetType().FullName ?? value.GetType().Name);
        WriteUtf8Literal(ref writer, ":"u8);
        WriteUtf8String(ref writer, value.ToString());
    }

    private static void WriteComplexObject(ref ArrayBufferWriter<byte> writer, object arg)
    {
        byte[] serializedArg;
        try
        {
            serializedArg = JsonSerializer.SerializeToUtf8Bytes(arg, _jsonOptions.GetTypeInfo(arg.GetType()));
        }
        catch (Exception ex)
        {
            // Falling back to ToString() would give different values the same key and return the wrong data.
            throw new InvalidOperationException(
                $"MethodCache cannot build a cache key from an argument of type '{arg.GetType().FullName}'. " +
                $"Implement {nameof(ICacheKeyProvider)} on the type, or use a serializable type.", ex);
        }

        WriteUtf8Literal(ref writer, "_OBJ:"u8);
        WriteUtf8String(ref writer, arg.GetType().FullName ?? arg.GetType().Name);
        WriteUtf8Literal(ref writer, ":"u8);
        WriteHex(ref writer, serializedArg);
    }

    private static void WriteHex(ref ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> data)
    {
        var span = writer.GetSpan(data.Length * 2);

        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i];
            span[i * 2] = HexChars[b >> 4];
            span[i * 2 + 1] = HexChars[b & 0xF];
        }

        writer.Advance(data.Length * 2);
    }
    
    private static string ToHexString(UInt128 hash)
    {
        Span<char> chars = stackalloc char[32];
        for (var i = 31; i >= 0; i--)
        {
            chars[i] = "0123456789abcdef"[(int)(hash & 0xF)];
            hash >>= 4;
        }

        return new string(chars);
    }
}
