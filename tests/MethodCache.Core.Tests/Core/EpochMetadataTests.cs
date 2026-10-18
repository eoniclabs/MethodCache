using System.Buffers.Binary;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class EpochMetadataTests
{
    [Fact]
    public void Ensure_PreservesExistingEpochAndMethodStamp()
    {
        var stamp = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(stamp, 1234);
        var seed = Guid.NewGuid();
        var data = EpochMetadata.Ensure(stamp, seed);
        Assert.True(EpochMetadata.TryRead(data, out var epoch));
        Assert.Equal(seed, epoch.Initialization);
        Assert.Equal(0UL, epoch.Revision);
        Assert.Equal(1234, EpochMetadata.Stamp(data));
        Assert.Equal(data, EpochMetadata.Ensure(data, Guid.NewGuid()));
        Assert.Equal(1234, EpochMetadata.Stamp(EpochMetadata.WithStamp(data, 1200, Guid.NewGuid())));
        Assert.Equal(1300, EpochMetadata.Stamp(EpochMetadata.WithStamp(data, 1300, Guid.NewGuid())));
    }

    [Fact]
    public void MarkedStampUpdate_AdvancesTheEpochInTheSameValue()
    {
        var seed = Guid.NewGuid();
        var marked = EpochMetadata.Ensure(null, seed);
        var updated = EpochMetadata.WithStamp(marked, 100, Guid.NewGuid());
        Assert.True(EpochMetadata.TryRead(updated, out var result));
        Assert.Equal(seed, result.Initialization);
        Assert.Equal(1UL, result.Revision);
        Assert.Equal(100, EpochMetadata.Stamp(updated));
        Assert.Equal(long.MinValue, EpochMetadata.Stamp(marked));
    }

    [Fact]
    public void OverflowAndMissingMarkers_CannotRestoreAnEarlierIdentity()
    {
        var original = EpochMetadata.Ensure(null, Guid.NewGuid());
        BinaryPrimitives.WriteUInt64BigEndian(original.AsSpan(25), ulong.MaxValue);
        var nextId = Guid.NewGuid();
        var next = EpochMetadata.Advance(original, nextId);
        Assert.True(EpochMetadata.TryRead(next, out var advanced));
        Assert.Equal(nextId, advanced.Initialization);
        Assert.Equal(0UL, advanced.Revision);
        var retried = EpochMetadata.Advance(next, Guid.NewGuid());
        Assert.True(EpochMetadata.TryRead(retried, out var retry));
        Assert.Equal(nextId, retry.Initialization);
        Assert.Equal(1UL, retry.Revision);
        var missing = EpochMetadata.Advance(null, Guid.NewGuid());
        Assert.True(EpochMetadata.TryRead(missing, out var recreated));
        Assert.NotEqual(advanced.Initialization, recreated.Initialization);
    }

    [Fact]
    public void OrdinaryStamps_KeepMaximumTimestamp_AndMalformedMetadataFailsClosed()
    {
        Assert.Equal(long.MinValue, EpochMetadata.Stamp(null));
        var previous = EpochMetadata.WithStamp(null, 100, Guid.NewGuid());
        Assert.Equal(100, EpochMetadata.Stamp(EpochMetadata.WithStamp(previous, 90, Guid.NewGuid())));
        Assert.Equal(long.MaxValue, EpochMetadata.Stamp([1, 2, 3]));
        Assert.Throws<IncompatibleDistributedScopeException>(() => EpochMetadata.Ensure([1], Guid.NewGuid()));
        Assert.Throws<IncompatibleDistributedScopeException>(() => EpochMetadata.WithStamp([1], 100, Guid.NewGuid()));
        Assert.False(EpochMetadata.TryRead(new byte[EpochMetadata.Size], out _));
    }
}
