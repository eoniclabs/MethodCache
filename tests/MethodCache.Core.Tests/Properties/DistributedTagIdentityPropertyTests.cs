using CsCheck;
using MethodCache.Core.Tests.Core;
using MethodCache.Core.Tests.FaultInjection;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

[Collection(nameof(CpuIntensiveCollection))]
public class DistributedTagIdentityPropertyTests
{
    [Fact]
    public void TransportDecode_AvoidsSubstringAndIntermediateCharacterArrayAllocations()
    {
        var value = new string('\ud800', 8192);
        var wire = LosslessWireText.Encode(value);
        Assert.True(LosslessWireText.TryDecode(wire, out _));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var correct = true;
        for (var i = 0; i < 10; i++) correct &= LosslessWireText.TryDecode(wire, out var decoded) && decoded == value;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(correct);
        Assert.True(allocated < value.Length * sizeof(char) * 3L * 10, $"Allocated {allocated} bytes while decoding ten frames.");
    }

    [Theory]
    [InlineData("old")]
    [InlineData("4:not-base64")]
    [InlineData("4:AA==")]
    public void MalformedTransportText_IsRejected(string wire) => Assert.False(LosslessWireText.TryDecode(wire, out _));

    [Fact]
    public void MethodIdentity_PreservesOrdinalUtf16ThroughReplacingEncodings()
    {
        Gen.String[Gen.Char, 0, 32].Sample(text =>
        {
            var wire = LosslessWireText.Encode(text);
            foreach (var encoding in new[] { System.Text.Encoding.UTF8, System.Text.Encoding.Unicode })
            {
                var transported = encoding.GetString(encoding.GetBytes(wire));
                Assert.True(LosslessWireText.TryDecode(transported, out var decoded));
                Assert.Equal(text, decoded);
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void WireContractOne_PreservesEveryOrdinalTargetAndOperation()
    {
        Gen.String[Gen.Char, 0, 128].Sample(text =>
        {
            foreach (var type in Enum.GetValues<MethodCache.Extensibility.BackplaneMessageType>())
            {
                var frame = BackplaneWire.Encode(new MethodCache.Extensibility.BackplaneMessage
                { Type = type, InstanceId = "sender", Key = text, Tag = text, Timestamp = DateTimeOffset.UnixEpoch });
                var transported = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(frame));
                Assert.True(BackplaneWire.TryDecode(transported, out var result));
                Assert.Equal(type, result.Type);
                Assert.Equal(type is MethodCache.Extensibility.BackplaneMessageType.KeyInvalidation or MethodCache.Extensibility.BackplaneMessageType.MethodInvalidation ? text : null, result.Key);
                Assert.Equal(type == MethodCache.Extensibility.BackplaneMessageType.TagInvalidation ? text : null, result.Tag);
            }
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public async Task ColdNode_PreservesExactTagIdentityAfterInvalidation()
    {
        var text = Gen.String[Gen.Frequency((1, Gen.Char), (2, Gen.Char["\0\ufffd\ud800\ud801\udfffabc"])), 0, 12];
        await text.SampleAsync(async suffix =>
        {
            var clock = new FakeTimeProvider();
            var backend = new ExpiringAtomicCache(clock);
            ServiceProvider Node()
            {
                var services = new ServiceCollection();
                services.AddSingleton<TimeProvider>(clock);
                services.AddMethodCache();
                services.AddMethodCacheDistributedCache(_ => backend, options =>
                {
                    options.ClockSkewTolerance = TimeSpan.Zero;
                    options.TagCheckInterval = TimeSpan.Zero;
                });
                return services.BuildServiceProvider();
            }
            using var a = Node();
            using var b = Node();
            using var c = Node();
            var tag = "tag:" + suffix;
            var policy = CacheRuntimePolicy.Empty("identity") with { Duration = TimeSpan.FromMinutes(1), Tags = [tag] };
            Assert.Equal("old", await a.GetRequiredService<ICacheManager>().GetOrCreateFastAsync("key", "identity", () => Task.FromResult("old"), policy));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await b.GetRequiredService<ICacheManager>().InvalidateByTagsAsync(tag);
            Assert.Equal("new", await c.GetRequiredService<ICacheManager>().GetOrCreateFastAsync("key", "identity", () => Task.FromResult("new"), policy));
        }, seed: PropertySettings.Seed, iter: PropertySettings.ConcurrentIterations);
    }
}
