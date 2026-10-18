using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Infrastructure;

namespace MethodCache.Extensibility;

/// <summary>
/// Default cache key generator. Writes the method name, version and arguments into a canonical binary form and
/// hashes it with 128-bit XxHash3.
/// </summary>
/// <remarks>
/// Keys have the form <c>{methodName}[:v{version}]:{hash}</c>, so all entries of one method share a readable
/// prefix that pattern invalidation can match (see <see cref="MethodCacheKeys"/>). Every argument is written as a
/// type tag followed by its value, and variable-length values by their length first, so two different argument
/// lists never produce the same input to the hash. Built-in value types (<see cref="nint"/> and <see cref="nuint"/>
/// included) are written in binary, enums by their numeric value, dates with full precision. Other types are serialized with System.Text.Json, public fields
/// included, and a value inside them whose runtime type differs from its declared type (an element of
/// <c>object[]</c>, a base-typed property) with that type's name and members; a type whose state is all
/// non-public is refused, since every value of it would share one key.
/// Implement <see cref="ICacheKeyProvider"/> on such types, on types that cannot be serialized, or under Native AOT.
/// </remarks>
/// <example>
/// <code>
/// // FastHashKeyGenerator is the default; register it explicitly to replace another generator
/// services.Replace(ServiceDescriptor.Singleton&lt;ICacheKeyGenerator, FastHashKeyGenerator&gt;());
/// </code>
/// </example>
internal class FastHashKeyGenerator : ICacheKeyGenerator
{
    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_writer;

    private enum Tag : byte
    {
        Null = 1,
        KeyProvider,
        String,
        Boolean,
        Char,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        DateTime,
        DateTimeOffset,
        TimeSpan,
        Guid,
        Enum,
        Object,
        IntPtr, // after Object, so the tags above, and the keys built with them, stay the same
        UIntPtr,
    }

    /// <inheritdoc />
    public string GenerateKey(string methodName, object?[] args, CacheRuntimePolicy policy)
        => GenerateKey(methodName, args, policy.Version);

    internal sealed class PreparedPrefix(string methodName, int? version, string logicalPrefix, byte[] bytes)
    {
        internal string MethodName { get; } = methodName;
        internal int? Version { get; } = version;
        internal string LogicalPrefix { get; } = logicalPrefix;
        internal ReadOnlySpan<byte> Bytes => bytes;
    }

    internal static PreparedPrefix PreparePrefix(string methodName, int? version)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteString(writer, methodName);
        WriteInt32(writer, version ?? -1);
        var prefix = version.HasValue ? string.Concat(methodName, ":v", version.Value.ToString(CultureInfo.InvariantCulture)) : methodName;
        return new PreparedPrefix(methodName, version, prefix, writer.WrittenSpan.ToArray());
    }

    internal string GenerateKey(PreparedPrefix prefix, object?[] args)
        => GenerateKey(prefix.MethodName, args, prefix.Version, prefix);

    private static string GenerateKey(string methodName, object?[] args, int? version, PreparedPrefix? prepared = null)
    {
        var writer = t_writer ?? new ArrayBufferWriter<byte>(256);
        // Key providers and JSON getters may call GenerateKey recursively. The active writer belongs to this
        // invocation only; a nested invocation must borrow a different buffer.
        t_writer = null;
        writer.ResetWrittenCount();
        try
        {
            if (prepared == null)
            {
                WriteString(writer, methodName);
                WriteInt32(writer, version ?? -1);
            }
            else
            {
                prepared.Bytes.CopyTo(writer.GetSpan(prepared.Bytes.Length));
                writer.Advance(prepared.Bytes.Length);
            }
            WriteInt32(writer, args.Length);
            foreach (var arg in args)
            {
                WriteArgument(writer, arg);
            }

            var hash = XxHash128.HashToUInt128(writer.WrittenSpan);
            var prefix = prepared?.LogicalPrefix ?? (version.HasValue
                ? string.Concat(methodName, ":v", version.Value.ToString(CultureInfo.InvariantCulture))
                : methodName);
            return string.Create(prefix.Length + 33, (Prefix: prefix, Hash: hash), static (text, state) =>
            {
                state.Prefix.AsSpan().CopyTo(text);
                text[state.Prefix.Length] = ':';
                _ = state.Hash.TryFormat(text[(state.Prefix.Length + 1)..], out _, "x32", CultureInfo.InvariantCulture);
            });
        }
        finally
        {
            if (writer.Capacity <= 64 * 1024)
            {
                // Return the buffer after failures too, but never retain one grown by a huge argument.
                t_writer = writer;
            }
        }
    }

    private static void WriteArgument(ArrayBufferWriter<byte> writer, object? arg)
    {
        switch (arg)
        {
            case null:
                WriteTag(writer, Tag.Null);
                break;
            case ICacheKeyProvider keyProvider:
                WriteTag(writer, Tag.KeyProvider);
                WriteString(writer, keyProvider.CacheKeyPart ?? string.Empty);
                break;
            case string s:
                WriteTag(writer, Tag.String);
                WriteString(writer, s);
                break;
            case bool b:
                WriteTag(writer, Tag.Boolean);
                WriteByte(writer, b ? (byte)1 : (byte)0);
                break;
            case char c:
                WriteTag(writer, Tag.Char);
                WriteUInt64(writer, c);
                break;
            case byte b:
                WriteTag(writer, Tag.Byte);
                WriteByte(writer, b);
                break;
            case sbyte sb:
                WriteTag(writer, Tag.SByte);
                WriteInt64(writer, sb);
                break;
            case short sh:
                WriteTag(writer, Tag.Int16);
                WriteInt64(writer, sh);
                break;
            case ushort us:
                WriteTag(writer, Tag.UInt16);
                WriteUInt64(writer, us);
                break;
            case int i:
                WriteTag(writer, Tag.Int32);
                WriteInt32(writer, i);
                break;
            case uint ui:
                WriteTag(writer, Tag.UInt32);
                WriteUInt64(writer, ui);
                break;
            case long l:
                WriteTag(writer, Tag.Int64);
                WriteInt64(writer, l);
                break;
            case ulong ul:
                WriteTag(writer, Tag.UInt64);
                WriteUInt64(writer, ul);
                break;
            case nint n:
                // Widened, so a value has the same key in 32-bit and 64-bit processes
                WriteTag(writer, Tag.IntPtr);
                WriteInt64(writer, n);
                break;
            case nuint un:
                WriteTag(writer, Tag.UIntPtr);
                WriteUInt64(writer, un);
                break;
            case float f:
                WriteTag(writer, Tag.Single);
                // -0 and 0 are different inputs (1 / x tells them apart), as on the inline key path; every NaN is one key
                WriteInt32(writer, BitConverter.SingleToInt32Bits(float.IsNaN(f) ? float.NaN : f));
                break;
            case double d:
                WriteTag(writer, Tag.Double);
                WriteInt64(writer, BitConverter.DoubleToInt64Bits(double.IsNaN(d) ? double.NaN : d));
                break;
            case decimal dec:
                // The string keeps the scale, so 1.0m and 1.00m are different keys, as decimal.ToString shows them
                WriteTag(writer, Tag.Decimal);
                WriteString(writer, dec.ToString(CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                // ToBinary() would embed the server's local offset; normalize so every server agrees.
                WriteTag(writer, Tag.DateTime);
                var normalized = dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
                WriteInt64(writer, normalized.Ticks);
                WriteByte(writer, (byte)normalized.Kind);
                break;
            case DateTimeOffset dto:
                WriteTag(writer, Tag.DateTimeOffset);
                WriteInt64(writer, dto.UtcTicks);
                WriteInt64(writer, dto.Offset.Ticks);
                break;
            case TimeSpan ts:
                WriteTag(writer, Tag.TimeSpan);
                WriteInt64(writer, ts.Ticks);
                break;
            case Guid guid:
                WriteTag(writer, Tag.Guid);
                guid.TryWriteBytes(writer.GetSpan(16));
                writer.Advance(16);
                break;
            case Enum enumValue:
                // By number, like generated keys, so renaming a member keeps existing keys
                WriteTag(writer, Tag.Enum);
                WriteString(writer, TypeName(enumValue.GetType()));
                WriteString(writer, EnumNumber(enumValue));
                break;
            default:
                WriteTag(writer, Tag.Object);
                WriteString(writer, TypeName(KeyArguments.KeyType(arg)));
                WriteBytes(writer, KeyArguments.Serialize(arg));
                break;
        }
    }

    // Without the assembly versions FullName puts in generic arguments, so keys stay the same across runtime updates
    // (and instances on different patch versions share one distributed cache)
    internal static string TypeName(Type type) => KeyArguments.TypeName(type);

    /// <summary>
    /// The enum's numeric value in the invariant culture. Not <c>ToString("D")</c>, which writes a negative value
    /// with the current culture's minus sign (U+2212 in sv-SE), so the key would depend on the thread's culture.
    /// </summary>
    private static string EnumNumber(Enum value) => Type.GetTypeCode(value.GetType()) switch
    {
        TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64
            => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => value.ToString("D"), // unsigned: no sign to format
    };

    private static void WriteTag(ArrayBufferWriter<byte> writer, Tag tag) => WriteByte(writer, (byte)tag);

    private static void WriteByte(ArrayBufferWriter<byte> writer, byte value)
    {
        writer.GetSpan(1)[0] = value;
        writer.Advance(1);
    }

    private static void WriteInt32(ArrayBufferWriter<byte> writer, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }

    private static void WriteInt64(ArrayBufferWriter<byte> writer, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }

    private static void WriteUInt64(ArrayBufferWriter<byte> writer, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }

    /// <summary>Writes the UTF-8 length, then the UTF-8 bytes, so no string can run into the next value.</summary>
    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var span = writer.GetSpan(4 + byteCount);
        if (Utf8.FromUtf16(value, span[4..], out _, out var written, replaceInvalidSequences: false) == OperationStatus.Done)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span, written);
            writer.Advance(4 + written);
            return;
        }

        // A lone surrogate: UTF-8 would write every one as U+FFFD, giving different strings one key. Write the UTF-16
        // code units instead, after a negative length that no UTF-8 length can equal.
        WriteInt32(writer, -1 - value.Length);
        foreach (var c in value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(2), c);
            writer.Advance(2);
        }
    }

    private static void WriteBytes(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        WriteInt32(writer, value.Length);
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

}
