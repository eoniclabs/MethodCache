using System.Collections.Concurrent;
using System.Globalization;
using CsCheck;
using MethodCache.Core.Infrastructure.Utilities;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class SamplingEstimatePropertyTests
{
    [Fact]
    public void Sampling_UniformPayloads_HasExactScalingAndBoundedSerialization()
    {
        Gen.Int.Sample(input =>
        {
            var count = (int)((uint)input % 257);
            var cache = new ConcurrentDictionary<string, int>();
            for (var i = 0; i < count; i++) cache["entry:" + i.ToString("D8", CultureInfo.InvariantCulture)] = 42;
            var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions
            {
                MemoryCalculationMode = MemoryUsageCalculationMode.Sampling,
                SamplingPercentage = 0.5,
                SamplingMaxEntries = 16
            }, new Random(42));
            var serialized = 0;
            var size = calculator.CalculateMemoryUsage(cache, value => { serialized++; return value; });
            Assert.Equal(count * (120L + 14 + 52), size);
            Assert.InRange(serialized, count == 0 ? 0 : 1, Math.Min(count, 16));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }
}
