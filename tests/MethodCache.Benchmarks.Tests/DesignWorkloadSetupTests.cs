using MethodCache.Benchmarks.Microbenchmarks;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class DesignWorkloadSetupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MemoryScenarios_ProveResidentHitsEvictionAndSingleFlight(bool weighted, bool listening)
    {
        var benchmark = new DesignMemoryWorkloadBenchmarks { Weighted = weighted, Listening = listening, SelectPerformanceCores = false };
        try
        {
            await benchmark.Setup();
            Assert.Equal(42, await benchmark.Hit());
            await benchmark.ContendedHits();
            Assert.Equal(168, await benchmark.SingleFlightBatch());
            Assert.Equal(1, benchmark.OriginLoads);
            for (var i = 0; i < 256; i++) Assert.Equal(42, await benchmark.EvictionPressure());
            Assert.Equal(168, await benchmark.SingleFlightBatch());
            Assert.Equal(2, benchmark.OriginLoads);
        }
        finally { benchmark.Cleanup(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpScenarios_ProveHitOrCookieBypassBeforeTiming(bool keepCookies)
    {
        var benchmark = new DesignHttpWorkloadBenchmarks { Backend = "Local", KeepCookies = keepCookies, SelectPerformanceCores = false };
        try
        {
            await benchmark.Setup();
            var before = benchmark.OriginLoads;
            Assert.Equal(200, await benchmark.Fresh());
            Assert.Equal(before + (keepCookies ? 1 : 0), benchmark.OriginLoads);
            Assert.Equal(200, await benchmark.Miss());
            Assert.Equal(before + (keepCookies ? 2 : 1), benchmark.OriginLoads);
        }
        finally { await benchmark.Cleanup(); }
    }
}
