using System.Security.Cryptography;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.SqlServer.Tests;

public class PhysicalKeyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(4000)]
    public void PhysicalKey_IsTheHexSha256OfTheExactCodeUnits(int length)
    {
        // NULs and lone surrogates included: every code unit must reach the hash unchanged
        var key = string.Create(length, 0, static (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = (char)(i * 7919 % 0xFFFF);
        });
        Assert.Equal("mc:" + Convert.ToHexString(SHA256.HashData(LosslessUtf16.GetBytes(key))), SqlServerDistributedCache.GetPhysicalKey(key));
    }

    [Fact]
    public void PhysicalKey_AllocatesOnlyTheId()
    {
        const string key = "MethodCache:http:slot:1234:https://example.test/products?page=2";
        _ = SqlServerDistributedCache.GetPhysicalKey(key);
        const int count = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < count; i++) _ = SqlServerDistributedCache.GetPhysicalKey(key);
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / (double)count;
        Assert.True(allocated <= 160, $"Allocated {allocated} B per physical key.");
    }
}
