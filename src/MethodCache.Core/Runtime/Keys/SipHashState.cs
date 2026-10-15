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
        if (_tailBytes != 0)
        {
            while (_tailBytes < 8 && !data.IsEmpty)
            {
                _tail |= (ulong)data[0] << (8 * _tailBytes++);
                data = data[1..];
            }
            if (_tailBytes != 8) return;
            Compress(_tail);
            _tail = 0;
            _tailBytes = 0;
        }
        while (data.Length >= 8)
        {
            Compress(BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }
        foreach (var value in data) _tail |= (ulong)value << (8 * _tailBytes++);
    }

    internal readonly ulong Finish64()
    {
        var current = this; // finishing never mutates a reusable prefix state
        current.Compress(_tail | (_length << 56));
        current._v2 ^= 0xff;
        current.Round(); current.Round(); current.Round(); current.Round();
        return current._v0 ^ current._v1 ^ current._v2 ^ current._v3;
    }

    internal readonly int Finish32()
    {
        var hash = Finish64();
        return unchecked((int)(hash ^ (hash >> 32)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Compress(ulong block)
    {
        _v3 ^= block;
        Round(); Round();
        _v0 ^= block;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Round()
    {
        unchecked
        {
            _v0 += _v1; _v1 = BitOperations.RotateLeft(_v1, 13); _v1 ^= _v0;
            _v0 = BitOperations.RotateLeft(_v0, 32);
            _v2 += _v3; _v3 = BitOperations.RotateLeft(_v3, 16); _v3 ^= _v2;
            _v0 += _v3; _v3 = BitOperations.RotateLeft(_v3, 21); _v3 ^= _v0;
            _v2 += _v1; _v1 = BitOperations.RotateLeft(_v1, 17); _v1 ^= _v2;
            _v2 = BitOperations.RotateLeft(_v2, 32);
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
