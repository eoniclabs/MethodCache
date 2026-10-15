using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MethodCache.Core.Runtime.Keys;

// Incremental form of the randomized string hash used by .NET's collision-defense comparer.
// https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Marvin.cs
internal struct MarvinState(ulong seed)
{
    private uint _p0 = (uint)seed, _p1 = (uint)(seed >> 32), _tail;
    private int _tailBytes;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(ReadOnlySpan<byte> data)
    {
        var p0 = _p0; var p1 = _p1;
        if (_tailBytes != 0)
        {
            var take = Math.Min(4 - _tailBytes, data.Length);
            for (var i = 0; i < take; i++) _tail |= (uint)data[i] << (8 * (_tailBytes + i));
            _tailBytes += take;
            data = data[take..];
            if (_tailBytes != 4) return;
            p0 = unchecked(p0 + _tail);
            Block(ref p0, ref p1);
            _tailBytes = 0;
            _tail = 0;
        }
        while (data.Length >= 8)
        {
            p0 = unchecked(p0 + BinaryPrimitives.ReadUInt32LittleEndian(data));
            Block(ref p0, ref p1);
            p0 = unchecked(p0 + BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
            Block(ref p0, ref p1);
            data = data[8..];
        }
        if (data.Length >= 4)
        {
            p0 = unchecked(p0 + BinaryPrimitives.ReadUInt32LittleEndian(data));
            Block(ref p0, ref p1);
            data = data[4..];
        }
        _tail = 0;
        if (data.Length >= 2) _tail = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if ((data.Length & 1) != 0) _tail |= (uint)data[^1] << (8 * (data.Length & 2));
        _tailBytes = data.Length;
        _p0 = p0; _p1 = p1;
    }

    // Snapshots end on a UTF-16 code-unit boundary. Consume the suffix directly in scalar locals,
    // avoiding a copied mutable incremental state and its tail-byte loop on every cache hit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int FinishUtf16(ReadOnlySpan<char> suffix)
    {
        if (!BitConverter.IsLittleEndian)
        {
            var copy = this; copy.AppendUtf16(suffix); return copy.Finish32();
        }
        var p0 = _p0; var p1 = _p1;
        if (_tailBytes != 0)
        {
            if (suffix.IsEmpty) return Finish32();
            p0 = unchecked(p0 + (_tail | ((uint)suffix[0] << 16)));
            Block(ref p0, ref p1);
            suffix = suffix[1..];
        }
        var bytes = MemoryMarshal.AsBytes(suffix);
        while (bytes.Length >= 8)
        {
            p0 = unchecked(p0 + BinaryPrimitives.ReadUInt32LittleEndian(bytes)); Block(ref p0, ref p1);
            p0 = unchecked(p0 + BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..])); Block(ref p0, ref p1);
            bytes = bytes[8..];
        }
        if (bytes.Length >= 4)
        {
            p0 = unchecked(p0 + BinaryPrimitives.ReadUInt32LittleEndian(bytes)); Block(ref p0, ref p1);
            bytes = bytes[4..];
        }
        var tail = bytes.Length == 0 ? 0x80u : BinaryPrimitives.ReadUInt16LittleEndian(bytes) | 0x800000u;
        p0 = unchecked(p0 + tail); Block(ref p0, ref p1); Block(ref p0, ref p1);
        return unchecked((int)(p0 ^ p1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly int Finish32()
    {
        var p0 = unchecked(_p0 + (_tail | (0x80u << (8 * _tailBytes))));
        var p1 = _p1;
        Block(ref p0, ref p1); Block(ref p0, ref p1);
        return unchecked((int)(p0 ^ p1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Block(ref uint p0, ref uint p1)
    {
        unchecked
        {
            p1 ^= p0;
            p0 = BitOperations.RotateLeft(p0, 20);
            p0 += p1;
            p1 = BitOperations.RotateLeft(p1, 9);
            p1 ^= p0;
            p0 = BitOperations.RotateLeft(p0, 27);
            p0 += p1;
            p1 = BitOperations.RotateLeft(p1, 19);
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
    private static readonly MarvinState Initial = CreateInitial();
    private static MarvinState CreateInitial()
    {
        Span<byte> seed = stackalloc byte[8];
        RandomNumberGenerator.Fill(seed);
        return new MarvinState(BinaryPrimitives.ReadUInt64LittleEndian(seed));
    }

    internal static MarvinState Prefix(ReadOnlySpan<char> text)
    {
        var hash = Initial;
        hash.AppendUtf16(text);
        return hash;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Compute(ReadOnlySpan<char> text) => Prefix(text).Finish32();
}
