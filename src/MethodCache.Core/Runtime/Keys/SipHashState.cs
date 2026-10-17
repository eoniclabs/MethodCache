using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MethodCache.Core.Runtime.Keys;

// SipHash-1-3 (https://www.aumasson.jp/siphash/siphash.pdf), the keyed hash Python and Rust use for their hash
// tables. A generated key is hashed in two steps: its method prefix once per invocation snapshot (Append), and the
// arguments on every lookup (Finish). A state hashes one of two encodings of a key, never both:
// - UTF-16 (AppendUtf16, FinishUtf16): the code units in little-endian byte order, so an unfinished 8-byte block
//   holds 0-3 of them.
// - ASCII (TryAppendAscii, TryFinishAscii): one byte per code unit, for a key whose code units are all below 0x80.
//   Half the input, so half the compressions; an unfinished block holds 0-7 of them.
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

    // Returns false, leaving a state that must not be used, when a code unit is not ASCII
    internal bool TryAppendAscii(ReadOnlySpan<char> text)
    {
        var v0 = _v0; var v1 = _v1; var v2 = _v2; var v3 = _v3;
        ref var next = ref MemoryMarshal.GetReference(text);
        var count = text.Length;
        var used = _length & 7;
        _length = unchecked(_length + count);
        ulong high = 0;
        if (used != 0)
        {
            var take = Math.Min(8 - used, count);
            _tail |= PackAscii(ref next, take, ref high) << (8 * used);
            next = ref Unsafe.Add(ref next, take);
            count -= take;
            if (used + take < 8) return IsAscii(high);
            Compress(ref v0, ref v1, ref v2, ref v3, _tail);
        }
        while (count >= 8)
        {
            Compress(ref v0, ref v1, ref v2, ref v3, NarrowAscii(ref next, ref high));
            next = ref Unsafe.Add(ref next, 8);
            count -= 8;
        }
        _tail = PackAscii(ref next, count, ref high);
        _v0 = v0; _v1 = v1; _v2 = v2; _v3 = v3;
        return IsAscii(high);
    }

    // Like FinishUtf16 for the ASCII form. Checks the suffix while it hashes it and returns false, with a hash
    // that must not be used, when a code unit is not ASCII; the caller then hashes the UTF-16 form instead.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool TryFinishAscii(ReadOnlySpan<char> suffix, out int hash)
    {
        var v0 = _v0; var v1 = _v1; var v2 = _v2; var v3 = _v3;
        ref var next = ref MemoryMarshal.GetReference(suffix);
        var count = suffix.Length;
        var used = _length & 7;
        var block = _tail;
        ulong high = 0;
        // The last byte is the input length in bytes, one per code unit, modulo 256
        var lengthByte = (ulong)(uint)unchecked(_length + count) << 56;
        if (used != 0)
        {
            var take = Math.Min(8 - used, count);
            block |= PackAscii(ref next, take, ref high) << (8 * used);
            next = ref Unsafe.Add(ref next, take);
            count -= take;
            if (used + take == 8)
            {
                Compress(ref v0, ref v1, ref v2, ref v3, block);
                block = 0;
            }
        }
        while (count >= 8)
        {
            var units = NarrowAscii(ref next, ref high);
            // Stops at the first block that is not ASCII, so a key that falls back to UTF-16 is not hashed twice
            if (!IsAscii(high))
            {
                hash = 0;
                return false;
            }
            Compress(ref v0, ref v1, ref v2, ref v3, units);
            next = ref Unsafe.Add(ref next, 8);
            count -= 8;
        }
        block |= PackAscii(ref next, count, ref high) | lengthByte;
        Compress(ref v0, ref v1, ref v2, ref v3, block);
        v2 ^= 0xff;
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        Round(ref v0, ref v1, ref v2, ref v3);
        hash = unchecked((int)(v0 ^ v1 ^ v2 ^ v3));
        return IsAscii(high);
    }

    // Whether every code unit ORed into the four 16-bit lanes of units was below 0x80
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAscii(ulong units) => (units & 0xff80_ff80_ff80_ff80UL) == 0;

    // Eight code units as eight bytes, the first in the low bits. Collects their bits in high, so the caller can
    // tell whether every code unit was ASCII.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong NarrowAscii(ref char text, ref ulong high)
    {
        var low = Word(ref text);
        var upper = Word(ref Unsafe.Add(ref text, 4));
        high |= low | upper;
        return Narrow4(low) | Narrow4(upper) << 32;
    }

    // Up to seven code units as bytes, the first in the low bits: four, two and one at a time, so a short
    // remainder costs at most three reads. Reads nothing when count is 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackAscii(ref char text, int count, ref ulong high)
    {
        ulong packed = 0;
        var shift = 0;
        if ((count & 4) != 0)
        {
            var units = Word(ref text);
            high |= units;
            packed = Narrow4(units);
            text = ref Unsafe.Add(ref text, 4);
            shift = 32;
        }
        if ((count & 2) != 0)
        {
            ulong units = text | (uint)Unsafe.Add(ref text, 1) << 16;
            high |= units;
            packed |= ((units | units >> 8) & 0xffff) << shift;
            text = ref Unsafe.Add(ref text, 2);
            shift += 16;
        }
        if ((count & 1) != 0)
        {
            ulong unit = text;
            high |= unit;
            packed |= unit << shift;
        }
        return packed;
    }

    // Four code units, each below 0x100, as four bytes
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Narrow4(ulong units)
    {
        units = (units | units >> 8) & 0x0000_ffff_0000_ffffUL;
        return (units | units >> 16) & 0xffff_ffffUL;
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

// A method prefix hashed in both encodings. A key takes the ASCII form only when all of it is ASCII, the prefix
// included, and the UTF-16 form otherwise, the same choice ProcessKeyHash.Compute makes for the whole text.
internal readonly struct KeyPrefixHash
{
    private readonly SipHashState _ascii; // Unused when the prefix is not ASCII
    private readonly SipHashState _utf16;
    private readonly bool _isAscii;

    internal KeyPrefixHash(SipHashState ascii, SipHashState utf16, bool isAscii)
    {
        _ascii = ascii;
        _utf16 = utf16;
        _isAscii = isAscii;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Finish(ReadOnlySpan<char> suffix)
        => _isAscii && _ascii.TryFinishAscii(suffix, out var hash) ? hash : FinishUtf16(suffix);

    // Out of line, so the ASCII hit path stays small where it is inlined
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int FinishUtf16(ReadOnlySpan<char> suffix) => _utf16.FinishUtf16(suffix);
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

    // Each encoding has its own secret key, so an ASCII key and a UTF-16 key with the same bytes are unrelated
    private static readonly SipHashState AsciiInitial = CreateInitial();
    private static readonly SipHashState Utf16Initial = CreateInitial();
    private static SipHashState CreateInitial()
    {
        Span<byte> key = stackalloc byte[16];
        RandomNumberGenerator.Fill(key);
        return new SipHashState(BinaryPrimitives.ReadUInt64LittleEndian(key), BinaryPrimitives.ReadUInt64LittleEndian(key[8..]));
    }

    internal static KeyPrefixHash Prefix(ReadOnlySpan<char> text) => Prefix(text, AsciiInitial, Utf16Initial);

    internal static KeyPrefixHash Prefix(ReadOnlySpan<char> text, SipHashState asciiInitial, SipHashState utf16Initial)
    {
        var ascii = asciiInitial;
        var isAscii = ascii.TryAppendAscii(text);
        var utf16 = utf16Initial;
        utf16.AppendUtf16(text);
        return new KeyPrefixHash(ascii, utf16, isAscii);
    }

    // Out of line: the memo and the prefix hash answer most calls, and an inlined copy of both encodings made its
    // callers spill the hash state to the stack
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Compute(ReadOnlySpan<char> text)
        => AsciiInitial.TryFinishAscii(text, out var hash) ? hash : Utf16Initial.FinishUtf16(text);
}
