using System.Collections.Concurrent;
using CsCheck;
using MethodCache.Core.Infrastructure.Utilities;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class MemoryEstimatePropertyTests
{
    [Theory]
    [InlineData("bytes")]
    [InlineData("string")]
    [InlineData("array")]
    [InlineData("collection")]
    public void FastEstimate_TracksChangingPayloadLengths_ThroughTheValueExtractor(string kind)
    {
        Gen.Int.Sample(value =>
        {
            var length = (int)((uint)value % 8192) + 1;
            var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions());
            var cache = new ConcurrentDictionary<string, Box>();
            object Payload(int size) => kind switch
            {
                "bytes" => new byte[size],
                "string" => new string('x', size),
                "array" => new object[size],
                _ => new List<object>(Enumerable.Repeat<object>(1, size))
            };
            cache["key"] = new Box(Payload(length));
            var small = calculator.CalculateMemoryUsage(cache, box => box.Value);
            cache["key"] = new Box(Payload(length + 1));
            var large = calculator.CalculateMemoryUsage(cache, box => box.Value);
            Assert.True(large > small, $"{kind}: {small} -> {large}");
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void AccurateMode_IntermediateFastEstimates_TrackPayloadGrowth()
    {
        var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions
        {
            MemoryCalculationMode = MemoryUsageCalculationMode.Accurate,
            AccurateModeRecalculationInterval = 100
        });
        var cache = new ConcurrentDictionary<string, Box>();
        cache["key"] = new Box(new byte[1]);
        var small = calculator.CalculateMemoryUsage(cache, box => box.Value);
        cache["key"] = new Box(new byte[1024 * 1024]);
        Assert.True(calculator.CalculateMemoryUsage(cache, box => box.Value) - small >= 1024 * 1024 - 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastEstimate_EnumAndNonMarshalableStructs_DoNotBreakStatistics(bool tuple)
    {
        var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions());
        var cache = new ConcurrentDictionary<string, object>();
        cache["key"] = tuple ? (1, "text") : MemoryUsageCalculationMode.Fast;
        Assert.True(calculator.CalculateMemoryUsage(cache, value => value) > 0);
    }

    private sealed record Box(object Value);
}
