using System.Collections.Concurrent;
using MethodCache.Core.Infrastructure.Utilities;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class SamplingWorkTests
{
    [Fact]
    public void Sampling_LargeCache_BoundsTheNumberOfSerializedPayloads()
    {
        var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions
        {
            MemoryCalculationMode = MemoryUsageCalculationMode.Sampling,
            SamplingPercentage = 1
        });
        var cache = new ConcurrentDictionary<string, int>(Enumerable.Range(0, 5000).Select(i => new KeyValuePair<string, int>(i.ToString(), i)));
        var serialized = 0;
        var estimate = calculator.CalculateMemoryUsage(cache, value => { serialized++; return value; });
        Assert.True(estimate > 0);
        Assert.InRange(serialized, 1, 1024);
    }

    [Fact]
    public void Sampling_RepeatedStatistics_ReusesThePreviousSample()
    {
        var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions { MemoryCalculationMode = MemoryUsageCalculationMode.Sampling });
        var cache = new ConcurrentDictionary<string, int>(Enumerable.Range(0, 100).Select(i => new KeyValuePair<string, int>(i.ToString(), i)));
        var serialized = 0;
        var first = calculator.CalculateMemoryUsage(cache, value => { serialized++; return value; });
        var before = serialized;
        var second = calculator.CalculateMemoryUsage(cache, value => { serialized++; return value; });
        Assert.Equal(first, second);
        Assert.Equal(before, serialized);
    }
    [Fact]
    public void Sampling_IntervalBoundaryAndEmptyCache_RecalculateOnlyWhenRequired()
    {
        var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions
        {
            MemoryCalculationMode = MemoryUsageCalculationMode.Sampling,
            SamplingMaxEntries = 4,
            SamplingPercentage = 1,
            SamplingRecalculationInterval = 2
        }, new Random(42));
        var cache = new ConcurrentDictionary<string, int>(Enumerable.Range(0, 100).Select(i => new KeyValuePair<string, int>(i.ToString(), 42)));
        var reads = 0;
        object Extract(int value) { reads++; return value; }
        calculator.CalculateMemoryUsage(cache, Extract);
        calculator.CalculateMemoryUsage(cache, Extract);
        Assert.Equal(4, reads);
        calculator.CalculateMemoryUsage(cache, Extract);
        Assert.Equal(8, reads);
        cache.Clear();
        Assert.Equal(0, calculator.CalculateMemoryUsage(cache, Extract));
        cache["new"] = 123;
        Assert.True(calculator.CalculateMemoryUsage(cache, Extract) > 0);
        Assert.Equal(9, reads);
    }

    [Fact]
    public void Sampling_SeededReservoir_ReachesEntriesBeyondTheFirstSample()
    {
        var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions
        {
            MemoryCalculationMode = MemoryUsageCalculationMode.Sampling,
            SamplingMaxEntries = 8,
            SamplingPercentage = 1,
            SamplingRecalculationInterval = 1
        }, new Random(42));
        var cache = new ConcurrentDictionary<string, int>(Enumerable.Range(0, 100).Select(i => new KeyValuePair<string, int>(i.ToString(), i)));
        var measured = new HashSet<int>();
        for (var run = 0; run < 1000; run++)
        {
            calculator.CalculateMemoryUsage(cache, value => { measured.Add(value); return value; });
        }
        Assert.Equal(100, measured.Count);
    }

    [Fact]
    public void Sampling_PayloadReentersStatistics_DoesNotStartAnotherSerialization()
    {
        var calculator = new MemoryUsageCalculator(new MethodCacheMemoryOptions
        {
            MemoryCalculationMode = MemoryUsageCalculationMode.Sampling,
            SamplingRecalculationInterval = 1
        });
        var cache = new ConcurrentDictionary<string, object>();
        var payload = new ReentrantPayload(() => calculator.CalculateMemoryUsage(cache, value => value));
        cache["key"] = payload;
        Assert.True(calculator.CalculateMemoryUsage(cache, value => value) > 0);
        Assert.Equal(1, payload.Reads);
    }

    private sealed class ReentrantPayload(Func<long> read)
    {
        public int Reads;
        public long Estimate => ++Reads > 1 ? 0 : read();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SamplingOptions_NonPositiveBounds_AreRejected(int value)
    {
        var options = new MethodCacheMemoryOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => options.SamplingMaxEntries = value);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.SamplingRecalculationInterval = value);
    }

    [Fact]
    public void SamplingOptions_NonFiniteFraction_IsRejected()
    {
        var options = new MethodCacheMemoryOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => options.SamplingPercentage = double.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.SamplingPercentage = double.PositiveInfinity);
    }
}
