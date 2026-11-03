using System.Security.Cryptography;
using FluentAssertions;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;

namespace MethodCache.PostgreSql.Tests;

public class PhysicalKeyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(257)]
    [InlineData(4000)]
    public void PhysicalKey_IsTheSha256OfTheExactCodeUnits(int length)
    {
        // NULs and lone surrogates included: every code unit must reach the hash unchanged
        var key = string.Create(length, 0, static (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = (char)(i * 7919 % 0xFFFF);
        });

        PostgreSqlDistributedCache.GetPhysicalKey(key).Should().Equal(SHA256.HashData(LosslessUtf16.GetBytes(key)));
    }

    [Theory]
    [InlineData("a", "A")]
    [InlineData("tag", "tag ")]
    [InlineData("\0clear", "\0\0clear")]
    [InlineData("\ud800", "�")]
    public void PhysicalKey_DistinctKeys_StayDistinct(string first, string second)
        => PostgreSqlDistributedCache.GetPhysicalKey(first).Should().NotEqual(PostgreSqlDistributedCache.GetPhysicalKey(second));

    [Fact]
    public void PhysicalKey_AllocatesOnlyTheId()
    {
        const string key = "MethodCache:http:slot:1234:https://example.test/products?page=2";
        _ = PostgreSqlDistributedCache.GetPhysicalKey(key);
        const int count = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++) _ = PostgreSqlDistributedCache.GetPhysicalKey(key);
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)count;
        Assert.True(allocated <= 64, $"Allocated {allocated} B per physical key.");
    }

    [Fact]
    public void Expiration_Absolute_IsKeptInUtc()
    {
        var at = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));

        PostgreSqlDistributedCache.Expiration(new DistributedCacheEntryOptions { AbsoluteExpiration = at })
            .Should().Be(at.UtcDateTime);
    }

    [Fact]
    public void Expiration_Relative_IsCountedFromNow()
    {
        var before = DateTime.UtcNow;
        var expires = PostgreSqlDistributedCache.Expiration(new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });

        expires.Should().BeOnOrAfter(before.AddMinutes(5)).And.BeOnOrBefore(DateTime.UtcNow.AddMinutes(5));
    }

    [Fact]
    public void Expiration_NoneOrBeyondWhatDotNetRepresents_IsForever()
    {
        PostgreSqlDistributedCache.Expiration(new DistributedCacheEntryOptions()).Should().Be(DateTime.MaxValue);
        PostgreSqlDistributedCache.Expiration(new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.MaxValue })
            .Should().Be(DateTime.MaxValue);
    }

    [Fact]
    public void Expiration_Sliding_IsNotSupported()
    {
        // MethodCache never asks for it; a silent absolute expiry would surprise anyone who did
        var act = () => PostgreSqlDistributedCache.Expiration(new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(1) });

        act.Should().Throw<NotSupportedException>();
    }
}
