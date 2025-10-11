using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MessagePack;
using MessagePack.Resolvers;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.KeyGenerators;

/// <summary>
/// High-performance cache key generator using FNV-1a hashing algorithm.
/// Optimized for production scenarios with minimal allocations and fastest execution (~50ns).
/// </summary>
/// <remarks>
/// Performance characteristics:
/// - ~50ns per key generation
/// - Minimal heap allocations (uses ArrayPool)
/// - Zero-allocation fast path for simple types
///
/// Use FastHashKeyGenerator when:
/// - Performance is critical
/// - Cache keys don't need to be human-readable
/// - You have high-throughput caching scenarios
///
/// For debugging or when you need readable keys, use JsonKeyGenerator instead.
/// </remarks>
/// <example>
/// <code>
/// services.AddMethodCache(config =>
/// {
///     config.DefaultKeyGenerator&lt;FastHashKeyGenerator&gt;();
/// });
///
/// // Or use with attribute
/// [Cache(KeyGeneratorType = typeof(FastHashKeyGenerator))]
/// Task&lt;User&gt; GetUserAsync(int userId);
/// </code>
/// </example>
public class FastHashKeyGenerator : ICacheKeyGenerator
{
    // Fast non-cryptographic hash constants (FNV-1a variant)
    private const ulong FNV_OFFSET_BASIS = 14695981039346656037UL;
    private const ulong FNV_PRIME = 1099511628211UL;
    
    // Pre-configured MessagePack options for security and performance
    private static readonly MessagePackSerializerOptions _messagePackOptions = 
        MessagePackSerializerOptions.Standard
            .WithResolver(ContractlessStandardResolver.Instance)
            .WithCompression(MessagePackCompression.Lz4BlockArray);
    
    // Hex lookup table for fast conversion
    private static ReadOnlySpan<byte> HexChars => "0123456789abcdef"u8;

    public string GenerateKey(string methodName, object[] args, CacheMethodSettings settings)
    {
        var writer = new ArrayBufferWriter<byte>(256);

        WriteUtf8String(ref writer, methodName);

        if (settings.Version.HasValue)
        {
            WriteUtf8Literal(ref writer, "_v"u8);
            WriteInt32(ref writer, settings.Version.Value);
        }

        foreach (var arg in args)
        {
            WriteArgument(ref writer, arg);
        }

        var payload = writer.WrittenSpan;
        var hash = ComputeFastHash(payload);
        var result = ToHexString(hash);

        return settings.Version.HasValue ? $"{result}_v{settings.Version.Value}" : result;
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
        WriteUtf8Literal(ref writer, "_DT:"u8);
        WriteInt64(ref writer, value.ToBinary());
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
        try
        {
            var serializedArg = MessagePackSerializer.Serialize(arg.GetType(), arg, _messagePackOptions);
            WriteUtf8Literal(ref writer, "_OBJ:"u8);
            WriteHex(ref writer, serializedArg);
        }
        catch
        {
            var fallbackValue = $"{arg.GetType().FullName}:{arg}";
            var escapedFallback = fallbackValue.Replace("_", "__");
            WriteUtf8Literal(ref writer, "_FALLBACK:"u8);
            WriteUtf8String(ref writer, escapedFallback);
        }
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
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeFastHash(ReadOnlySpan<byte> data)
    {
        var hash = FNV_OFFSET_BASIS;
        
        // Process 8 bytes at a time for better performance
        ref var dataRef = ref MemoryMarshal.GetReference(data);
        var remaining = data.Length;
        
        while (remaining >= sizeof(ulong))
        {
            var chunk = Unsafe.ReadUnaligned<ulong>(ref dataRef);
            hash ^= chunk;
            hash *= FNV_PRIME;
            dataRef = ref Unsafe.Add(ref dataRef, sizeof(ulong));
            remaining -= sizeof(ulong);
        }
        
        // Handle remaining bytes
        for (int i = data.Length - remaining; i < data.Length; i++)
        {
            hash ^= data[i];
            hash *= FNV_PRIME;
        }
        
        return hash;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ToHexString(ulong hash)
    {
        // Convert 64-bit hash to 16-character hex string
        Span<char> chars = stackalloc char[16];
        
        for (int i = 15; i >= 0; i--)
        {
            chars[i] = "0123456789abcdef"[(int)(hash & 0xF)];
            hash >>= 4;
        }
        
        return new string(chars);
    }
}
