using System.Buffers.Binary;

namespace MethodCache.Infrastructure;

internal readonly record struct CacheEpoch(Guid Initialization, ulong Revision);

// A normal method stamp is eight LE bytes. HTTP marks it permanently with v1 + opaque init-id + BE revision.
internal static class EpochMetadata
{
    internal const int Size = 33;
    internal const byte Version = 1;

    internal static bool TryRead(ReadOnlySpan<byte> data, out CacheEpoch epoch)
    {
        epoch = default;
        if (data.Length < Size || data[8] != Version) return false;
        epoch = new CacheEpoch(new Guid(data.Slice(9, 16)), BinaryPrimitives.ReadUInt64BigEndian(data.Slice(25, 8)));
        return true;
    }

    internal static byte[] Ensure(byte[]? previous, Guid seed)
    {
        if (previous != null && TryRead(previous, out _)) return previous.ToArray();
        if (previous is { Length: not 8 }) throw new IncompatibleDistributedScopeException("The distributed epoch metadata has an incompatible schema.");
        var data = new byte[Size];
        BinaryPrimitives.WriteInt64LittleEndian(data, previous == null ? long.MinValue : BinaryPrimitives.ReadInt64LittleEndian(previous));
        data[8] = Version;
        seed.TryWriteBytes(data.AsSpan(9, 16));
        return data;
    }

    internal static byte[] Advance(byte[]? previous, Guid seed)
    {
        var data = Ensure(previous, seed);
        _ = TryRead(data, out var epoch);
        if (epoch.Revision == ulong.MaxValue)
        {
            seed.TryWriteBytes(data.AsSpan(9, 16));
            BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(25), 0);
        }
        else BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(25), epoch.Revision + 1);
        return data;
    }

    internal static long Stamp(byte[]? data)
        => data == null ? long.MinValue : data.Length == 8 || TryRead(data, out _) ? BinaryPrimitives.ReadInt64LittleEndian(data) : long.MaxValue;

    internal static byte[] WithStamp(byte[]? previous, long stamp, Guid seed)
    {
        if (previous != null && TryRead(previous, out _))
        {
            var advanced = Advance(previous, seed);
            BinaryPrimitives.WriteInt64LittleEndian(advanced, Math.Max(stamp, Stamp(previous)));
            return advanced;
        }
        if (previous is { Length: not 8 }) throw new IncompatibleDistributedScopeException("The distributed invalidation metadata has an incompatible schema.");
        var data = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(data, Math.Max(stamp, Stamp(previous)));
        return data;
    }
}

internal sealed class IncompatibleDistributedScopeException(string message) : InvalidOperationException(message);
