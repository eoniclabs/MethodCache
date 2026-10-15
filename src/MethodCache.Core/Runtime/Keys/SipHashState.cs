using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MethodCache.Core.Runtime.Keys;

// SipHash-1-3 (https://www.aumasson.jp/siphash/siphash.pdf), the keyed hash Python and Rust use for their hash
// tables, over a key's UTF-16 code units in little-endian byte order. A generated key is hashed in two steps: its
// method prefix once per invocation snapshot (AppendUtf16), and the arguments on every lookup (FinishUtf16).
// Keys are whole code units, so an unfinished 8-byte block holds 0-3 of them.
internal struct SipHashState
{
    private ulong _v0, _v1, _v2, _v3;
    private ulong _tail; // The unfinished block's code units, the first in the low bits
    private int _length; // Code units appended so far

    internal SipHashState(ulong key0, ulong key1)
    {
        _v0 = 0x736f6d6570736575UL ^ key0;
        _v1 = 0x646f72616e646f6dUL ^ key1;
        _v2 = 0x6c7967656e657261UL ^ key0;
        _v3 = 0x7465646279746573UL ^ key1;
        _tail = 0;
        _length = 0;
    }

    internal void AppendUtf16(ReadOnlySpan<char> text)
    {
        var v0 = _v0; var v1 = _v1; var v2 = _v2; var v3 = _v3;
        var used = _length & 3;
        _length = unchecked(_length + text.Length);
        if (used != 0)
        {
            var take = Math.Min(4 - used, text.Length);
            _tail |= Pack(text[..take]) << (16 * used);
            text = text[take..];
            if (used + take < 4) return;
            Compress(ref v0, ref v1, ref v2, ref v3, _tail);
        }
        while (text.Length >= 4)
        {
            Compress(ref v0, ref v1, ref v2, ref v3, Word(text));
            text = text[4..];
        }
        _tail = Pack(text);
        _v0 = v0; _v1 = v1; _v2 = v2; _v3 = v3;
    }

    // Finishes a copy in locals, so a snapshot's prefix state is never changed by a lookup. Walks the suffix by
    // reference instead of slicing it, which keeps it in registers on the hit path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int FinishUtf16(ReadOnlySpan<char> suffix)
    {
        var v0 = _v0; var v1 = _v1; var v2 = _v2; var v3 = _v3;
        ref var next = ref MemoryMarshal.GetReference(suffix);
        var count = suffix.Length;
        var used = _length & 3;
        var block = _tail;
        // The last byte is the input length in bytes, two per code unit, modulo 256
        var lengthByte = (ulong)(uint)unchecked(_length + count) << 57;
        if (used != 0)
        {
            var take = Math.Min(4 - used, count);
            block |= Pack(ref next, take) << (16 * used);
            next = ref Unsafe.Add(ref next, take);
            count -= take;
            if (used + take == 4)
            {
                Compress(ref v0, ref v1, ref v2, ref v3, block);
                block = 0;
            }
        }
        while (count >= 4)
        {
            Compress(ref v0, ref v1, ref v2, ref v3, Word(ref next));
            next = ref Unsafe.Add(ref next, 4);
            count -= 4;
        }
        // Either the block is empty or the suffix is, so the rest needs no shift
        block |= Pack(ref next, count) | lengthByte;
        Compress(ref v0, ref v1, ref v2, ref v3, block);
        v2 ^= 0xff;
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        return unchecked((int)(v0 ^ v1 ^ v2 ^ v3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Word(ReadOnlySpan<char> text) => Word(ref MemoryMarshal.GetReference(text));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Word(ref char text)
    {
        if (BitConverter.IsLittleEndian) return Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<char, byte>(ref text));
        return text | (ulong)Unsafe.Add(ref text, 1) << 16 | (ulong)Unsafe.Add(ref text, 2) << 32 | (ulong)Unsafe.Add(ref text, 3) << 48;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Pack(ReadOnlySpan<char> text) => Pack(ref MemoryMarshal.GetReference(text), text.Length);

    // Up to three code units, the first in the low bits. Reads nothing when count is 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Pack(ref char text, int count)
    {
        if (count == 0) return 0;
        ulong packed = text;
        if (count > 1)
        {
            packed |= (ulong)Unsafe.Add(ref text, 1) << 16;
            if (count > 2) packed |= (ulong)Unsafe.Add(ref text, 2) << 32;
        }
        return packed;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Compress(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3, ulong block)
    {
        v3 ^= block;
        Round(ref v0, ref v1, ref v2, ref v3);
        v0 ^= block;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
    {
        unchecked
        {
            v0 += v1; v1 = BitOperations.RotateLeft(v1, 13); v1 ^= v0; v0 = BitOperations.RotateLeft(v0, 32);
            v2 += v3; v3 = BitOperations.RotateLeft(v3, 16); v3 ^= v2;
            v0 += v3; v3 = BitOperations.RotateLeft(v3, 21); v3 ^= v0;
            v2 += v1; v1 = BitOperations.RotateLeft(v1, 17); v1 ^= v2; v2 = BitOperations.RotateLeft(v2, 32);
        }
    }
}

internal static class ProcessKeyHash
{
    // One immutable string per thread avoids rehashing the same key through lookup/publication/eviction.
    // Reference identity is essential: equal text from a different owner must be independently hashed.
    // Large keys are never retained. This is process-local metadata, not a cache entry or external identity.
    [ThreadStatic] private static string? _lastText;
    [ThreadStatic] private static int _lastHash;
    internal static int Compute(string text)
    {
        if (ReferenceEquals(_lastText, text)) return _lastHash;
        var hash = Compute(text.AsSpan());
        Remember(text, hash);
        return hash;
    }
    internal static void Remember(string text, int hash)
    {
        if (text.Length > 2048) return;
        _lastText = text;
        _lastHash = hash;
    }
    private static readonly SipHashState Initial = CreateInitial();
    private static SipHashState CreateInitial()
    {
        Span<byte> key = stackalloc byte[16];
        RandomNumberGenerator.Fill(key);
        return new SipHashState(BinaryPrimitives.ReadUInt64LittleEndian(key), BinaryPrimitives.ReadUInt64LittleEndian(key[8..]));
    }

    internal static SipHashState Prefix(ReadOnlySpan<char> text)
    {
        var hash = Initial;
        hash.AppendUtf16(text);
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Compute(ReadOnlySpan<char> text) => Initial.FinishUtf16(text);
}
