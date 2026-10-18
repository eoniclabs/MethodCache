using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class CanonicalTagPartTests
{
    private enum Unsigned : ulong { Maximum = ulong.MaxValue }
    private sealed class PartProvider : ICacheKeyProvider { public string CacheKeyPart => "a:b"; }

    [Fact]
    public void ApplicationMetadata_CannotSelectLegacyFormatting()
    {
        var policy = CacheRuntimePolicy.Empty("Read") with
        { Tags = ["value:{id}"], Metadata = new Dictionary<string, string?> { ["tag-format"] = "legacy" } };
        Assert.Equal("value:" + MethodCacheKeys.Null, CacheTagTemplates.Expand(policy, ["id"], [(object?)null]).Tags.Single());
    }

    [Fact]
    public void TagTemplates_UseTheSameScalarPartsAsKeys()
    {
        var instant = new DateTimeOffset(2026, 10, 15, 12, 13, 14, TimeSpan.FromHours(2)).AddTicks(1234);
        var id = Guid.Parse("e4cf16cb-b221-4c3d-8231-d78a2e126978");
        (object? Value, string Part)[] cases =
        [
            (null, MethodCacheKeys.Null),
            ("a:b\\c\0", MethodCacheKeys.Part("a:b\\c\0")),
            (id, MethodCacheKeys.Part(id)),
            (instant, MethodCacheKeys.Part(instant)),
            (instant.UtcDateTime, MethodCacheKeys.Part(instant.UtcDateTime)),
            (DayOfWeek.Friday, MethodCacheKeys.Part(DayOfWeek.Friday)),
            (false, MethodCacheKeys.Part(false)),
            ('x', MethodCacheKeys.Part('x')),
            ((byte)1, MethodCacheKeys.Part((byte)1)),
            ((sbyte)-1, MethodCacheKeys.Part((sbyte)-1)),
            ((short)-2, MethodCacheKeys.Part((short)-2)),
            ((ushort)2, MethodCacheKeys.Part((ushort)2)),
            (-3, MethodCacheKeys.Part(-3)),
            (3U, MethodCacheKeys.Part(3U)),
            (-4L, MethodCacheKeys.Part(-4L)),
            (4UL, MethodCacheKeys.Part(4UL)),
            (1.5f, MethodCacheKeys.Part(1.5f)),
            (1.5d, MethodCacheKeys.Part(1.5d)),
            (1.5m, MethodCacheKeys.Part(1.5m)),
            (Unsigned.Maximum, MethodCacheKeys.Part(Unsigned.Maximum)),
            (new PartProvider(), MethodCacheKeys.Part("a:b")),
            (new Version(1, 2), MethodCacheKeys.Part("1.2"))
        ];
        var policy = CacheRuntimePolicy.Empty("Read") with { Tags = ["value:{p}"] };
        foreach (var item in cases)
            Assert.Equal("value:" + item.Part, CacheTagTemplates.Expand(policy, ["p"], [item.Value]).Tags.Single());
    }
}
