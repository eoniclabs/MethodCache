using System.Globalization;
using MethodCache.Generated;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// <c>MethodCacheKeys.Tag($"...")</c> writes each value the way a <c>[Cache]</c> tag template does, so a tag built by
/// hand names the same entries.
/// </summary>
public class MethodCacheKeysTagTests
{
    private enum Tier : short { Free = -1, Pro = 2 }

    [Fact]
    public void Tag_WritesEachValueLikeATagTemplate_InAnyCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            var tenant = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
            var at = new DateTime(2026, 11, 6, 15, 30, 0, DateTimeKind.Utc);
            var offset = new DateTimeOffset(2026, 11, 6, 16, 30, 0, TimeSpan.FromHours(1));
            string? missing = null;

            Assert.Equal("price:ollama:gemma3\\:4b", MethodCacheKeys.Tag($"price:{"ollama"}:{"gemma3:4b"}"));
            Assert.Equal(Expected(tenant, Tier.Free, true, 'c', -1.5, 2.25f, 12.5m, at, offset, missing),
                MethodCacheKeys.Tag($"{tenant}|{Tier.Free}|{true}|{'c'}|{-1.5}|{2.25f}|{12.5m}|{at}|{offset}|{missing}"));
            Assert.Equal(Expected((byte)1, (sbyte)-2, (short)-3, (ushort)4, -5, 6u, -7L, 8UL),
                MethodCacheKeys.Tag($"{(byte)1}|{(sbyte)-2}|{(short)-3}|{(ushort)4}|{-5}|{6u}|{-7L}|{8UL}"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NullableValues_WriteTheValueOrNull_InTagsAndParts()
    {
        int? count = 42;
        int? none = null;
        Guid? tenant = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        Guid? noTenant = null;
        Tier? tier = Tier.Pro;
        Tier? noTier = null;
        DateTime? at = new DateTime(2026, 11, 6, 15, 30, 0, DateTimeKind.Utc);

        Assert.Equal(Expected(42, null, tenant, null, Tier.Pro, null, at),
            MethodCacheKeys.Tag($"{count}|{none}|{tenant}|{noTenant}|{tier}|{noTier}|{at}"));
        Assert.Equal(KeyParts.TagPart(42), MethodCacheKeys.Part(count));
        Assert.Equal(KeyParts.Null, MethodCacheKeys.Part(none));
        Assert.Equal(KeyParts.TagPart(tenant), MethodCacheKeys.Part(tenant));
        Assert.Equal(KeyParts.Null, MethodCacheKeys.Part(noTier));
        Assert.Equal(KeyParts.TagPart(Tier.Pro), MethodCacheKeys.Part(tier));
    }

    private static string Expected(params object?[] values) => string.Join("|", values.Select(KeyParts.TagPart));
}
