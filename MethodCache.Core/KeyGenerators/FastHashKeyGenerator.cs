using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MessagePack;
using MessagePack.Resolvers;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.KeyGenerators;

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
        // Rent buffer from pool for efficient memory usage
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var span = buffer.AsSpan();
            var written = 0;
            
            // Write method name
            written += WriteUtf8String(span[written..], methodName);
            
            // Write version if present
            if (settings.Version.HasValue)
            {
                written += WriteUtf8String(span[written..], "_v"u8);
                written += WriteInt32(span[written..], settings.Version.Value);
            }
            
            // Process arguments efficiently
            foreach (var arg in args)
            {
                written += WriteArgument(span[written..], arg);
            }
            
            // Generate fast hash and convert to hex
            var hash = ComputeFastHash(span[..written]);
            var result = ToHexString(hash);
            
            return settings.Version.HasValue ? $"{result}_v{settings.Version.Value}" : result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteArgument(Span<byte> span, object? arg)
    {
        if (arg is null)
        {
            return WriteUtf8String(span, "_NULL"u8);
        }
        
        return arg switch
        {
            ICacheKeyProvider keyProvider => WriteUtf8String(span, $"_{keyProvider.CacheKeyPart}"),
            string s => WriteString(span, s),
            int i => WriteInt32WithPrefix(span, i, "_INT:"u8),
            long l => WriteInt64WithPrefix(span, l, "_LONG:"u8),
            bool b => WriteUtf8String(span, b ? "_BOOL:True"u8 : "_BOOL:False"u8),
            double d => WriteDouble(span, d),
            float f => WriteFloat(span, f), 
            decimal dec => WriteDecimal(span, dec),
            DateTime dt => WriteDateTime(span, dt),
            DateTimeOffset dto => WriteDateTimeOffset(span, dto),
            Guid guid => WriteGuid(span, guid),
            Enum enumValue => WriteEnum(span, enumValue),
            _ => WriteComplexObject(span, arg)
        };
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteUtf8String(Span<byte> span, ReadOnlySpan<byte> utf8)
    {
        utf8.CopyTo(span);
        return utf8.Length;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteUtf8String(Span<byte> span, string str)
    {
        return Encoding.UTF8.GetBytes(str, span);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteString(Span<byte> span, string s)
    {
        var written = WriteUtf8String(span, "_STR:"u8);
        // Escape underscores for security
        var escaped = s.Replace("_", "__");
        written += WriteUtf8String(span[written..], escaped);
        return written;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteInt32WithPrefix(Span<byte> span, int value, ReadOnlySpan<byte> prefix)
    {
        var written = WriteUtf8String(span, prefix);
        written += WriteInt32(span[written..], value);
        return written;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteInt64WithPrefix(Span<byte> span, long value, ReadOnlySpan<byte> prefix)
    {
        var written = WriteUtf8String(span, prefix);
        written += WriteInt64(span[written..], value);
        return written;
    }
    
    private static int WriteInt32(Span<byte> span, int value)
    {
        return WriteUtf8String(span, value.ToString());
    }
    
    private static int WriteInt64(Span<byte> span, long value)
    {
        return WriteUtf8String(span, value.ToString());
    }
    
    private static int WriteDouble(Span<byte> span, double value)
    {
        var written = WriteUtf8String(span, "_DBL:"u8);
        written += WriteUtf8String(span[written..], value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
        return written;
    }
    
    private static int WriteFloat(Span<byte> span, float value)
    {
        var written = WriteUtf8String(span, "_FLT:"u8);
        written += WriteUtf8String(span[written..], value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
        return written;
    }
    
    private static int WriteDecimal(Span<byte> span, decimal value)
    {
        var written = WriteUtf8String(span, "_DEC:"u8);
        written += WriteUtf8String(span[written..], value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return written;
    }
    
    private static int WriteDateTime(Span<byte> span, DateTime value)
    {
        var written = WriteUtf8String(span, "_DT:"u8);
        written += WriteInt64(span[written..], value.ToBinary());
        return written;
    }
    
    private static int WriteDateTimeOffset(Span<byte> span, DateTimeOffset value)
    {
        var written = WriteUtf8String(span, "_DTO:"u8);
        written += WriteInt64(span[written..], value.ToUnixTimeMilliseconds());
        written += WriteUtf8String(span[written..], ":"u8);
        written += WriteUtf8String(span[written..], value.Offset.TotalMinutes.ToString());
        return written;
    }
    
    private static int WriteGuid(Span<byte> span, Guid value)
    {
        var written = WriteUtf8String(span, "_GUID:"u8);
        written += WriteUtf8String(span[written..], value.ToString("N"));
        return written;
    }
    
    private static int WriteEnum(Span<byte> span, Enum value)
    {
        var written = WriteUtf8String(span, "_ENUM:"u8);
        written += WriteUtf8String(span[written..], value.GetType().FullName!);
        written += WriteUtf8String(span[written..], ":"u8);
        written += WriteUtf8String(span[written..], value.ToString());
        return written;
    }
    
    private static int WriteComplexObject(Span<byte> span, object arg)
    {
        try
        {
            // Use secure typed MessagePack serialization with ArrayPool
            var serializedArg = MessagePackSerializer.Serialize(arg.GetType(), arg, _messagePackOptions);
            var written = WriteUtf8String(span, "_OBJ:"u8);
            
            // Convert to hex for efficiency (faster than Base64)
            written += WriteHex(span[written..], serializedArg);
            return written;
        }
        catch
        {
            // Secure fallback
            var fallbackValue = $"{arg.GetType().FullName}:{arg}";
            var escapedFallback = fallbackValue.Replace("_", "__");
            var written = WriteUtf8String(span, "_FALLBACK:"u8);
            written += WriteUtf8String(span[written..], escapedFallback);
            return written;
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteHex(Span<byte> span, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            var b = data[i];
            span[i * 2] = HexChars[b >> 4];
            span[i * 2 + 1] = HexChars[b & 0xF];
        }
        return data.Length * 2;
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