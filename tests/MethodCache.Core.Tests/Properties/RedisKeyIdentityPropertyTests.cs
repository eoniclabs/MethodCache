using System.Text;
using CsCheck;
using MethodCache.Core.Tests.Core;
using MethodCache.Redis;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(CpuIntensiveCollection))]
public class RedisKeyIdentityPropertyTests
{
    [Fact]
    public void PhysicalKeys_PreserveOrdinalUtf16ThroughRedisUtf8Encoding()
    {
        var text = Gen.String[Gen.Frequency((1, Gen.Char), (2, Gen.Char["\\uD800\0\ud800\ud801\udfff\ufffdabc"])), 0, 24];
        Gen.Select(text, text).Sample((a, b) =>
        {
            var pa = RedisDistributedCache.GetPhysicalKey(a);
            var pb = RedisDistributedCache.GetPhysicalKey(b);
            Assert.Equal(a == b, Encoding.UTF8.GetBytes(pa).SequenceEqual(Encoding.UTF8.GetBytes(pb)));
            Assert.Equal(pa, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(pa)));
            Assert.Equal(pb, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(pb)));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void OrdinaryKeys_KeepTheirStringAndPrefix()
    {
        var key = "test:123:entry:å😀";
        Assert.Same(key, RedisDistributedCache.GetPhysicalKey(key));
        Assert.Throws<ArgumentNullException>(() => RedisDistributedCache.GetPhysicalKey(null!));
    }
}
