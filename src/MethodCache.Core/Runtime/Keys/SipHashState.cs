using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MethodCache.Core.Runtime.Keys;

// SipHash-2-4, as specified by Aumasson/Bernstein (https://www.aumasson.jp/siphash/siphash.pdf).
// This incremental implementation preserves incomplete blocks across arbitrary append boundaries.
internal struct SipHashState
{
    private ulong _v0, _v1, _v2, _v3, _tail, _length;
    private int _tailBytes;

    internal SipHashState(ulong key0, ulong key1)
    {
        _v0 = 0x736f6d6570736575UL ^ key0;
        _v1 = 0x646f72616e646f6dUL ^ key1;
        _v2 = 0x6c7967656e657261UL ^ key0;
        _v3 = 0x7465646279746573UL ^ key1;
        _tail = _length = 0;
        _tailBytes = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AppendUtf16(ReadOnlySpan<char> text)
    {
        if (BitConverter.IsLittleEndian) Append(MemoryMarshal.AsBytes(text));
        else
        {
            Span<byte> bytes = stackalloc byte[2];
            foreach (var character in text)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, character);
                Append(bytes);
            }
        }
    }

    internal void Append(ReadOnlySpan<byte> data)
    {
        _length = unchecked(_length + (ulong)data.Length);
        // Keep the round state in scalar locals. Mutating fields through the struct's this-reference
        // prevents the JIT from retaining them in registers across compression blocks.
        var v0 = _v0; var v1 = _v1; var v2 = _v2; var v3 = _v3;
        if (_tailBytes != 0)
        {
            var take = Math.Min(8 - _tailBytes, data.Length);
            _tail |= ReadTail(data[..take]) << (8 * _tailBytes);
            _tailBytes += take;
            data = data[take..];
            if (_tailBytes != 8) return;
            Compress(ref v0, ref v1, ref v2, ref v3, _tail);
            _tail = 0;
            _tailBytes = 0;
        }
        while (data.Length >= 8)
        {
            Compress(ref v0, ref v1, ref v2, ref v3, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }
        _tail = ReadTail(data);
        _tailBytes = data.Length;
        _v0 = v0; _v1 = v1; _v2 = v2; _v3 = v3;
    }

    internal readonly ulong Finish64()
    {
        var v0 = _v0; var v1 = _v1; var v2 = _v2; var v3 = _v3;
        Compress(ref v0, ref v1, ref v2, ref v3, _tail | (_length << 56));
        v2 ^= 0xff;
        Round(ref v0, ref v1, ref v2, ref v3); Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3); Round(ref v0, ref v1, ref v2, ref v3);
        return v0 ^ v1 ^ v2 ^ v3;
    }

    internal readonly int Finish32()
    {
        var hash = Finish64();
        return unchecked((int)(hash ^ (hash >> 32)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadTail(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        var shift = 0;
        if (data.Length >= 4)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(data);
            data = data[4..];
            shift = 32;
        }
        if (data.Length >= 2)
        {
            value |= (ulong)BinaryPrimitives.ReadUInt16LittleEndian(data) << shift;
            data = data[2..];
            shift += 16;
        }
        if (!data.IsEmpty) value |= (ulong)data[0] << shift;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Compress(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3, ulong block)
    {
        v3 ^= block;
        Round(ref v0, ref v1, ref v2, ref v3); Round(ref v0, ref v1, ref v2, ref v3);
        v0 ^= block;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        unchecked
        {
            v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0;
            v0 = BitOperations.RotateLeft(v0, 32);
            v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
            v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
            v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2;
            v2 = BitOperations.RotateLeft(v2, 32);
        }
    }

}

internal static class ProcessKeyHash
{
    private static readonly SipHashState Initial = CreateInitial();
    private static SipHashState CreateInitial()
    {
        Span<byte> seed = stackalloc byte[16];
        RandomNumberGenerator.Fill(seed);
        return new SipHashState(BinaryPrimitives.ReadUInt64LittleEndian(seed), BinaryPrimitives.ReadUInt64LittleEndian(seed[8..]));
    }

    internal static SipHashState Prefix(ReadOnlySpan<char> text)
    {
        var hash = Initial;
        hash.AppendUtf16(text);
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Compute(ReadOnlySpan<char> text) => Prefix(text).Finish32();
}
