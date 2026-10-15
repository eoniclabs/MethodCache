using MethodCache.Benchmarks.Microbenchmarks;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class DesignWorkloadSetupTests
{
    [Fact]
    public void HttpColdPublication_UsesAnExplicitBoundedBatch_WithIterationReset()
    {
        var cases = BenchmarkConverter.TypeToBenchmarks(typeof(HttpColdMissBenchmarks), DefaultConfig.Instance).BenchmarksCases;
        Assert.Equal(4, cases.Length);
        Assert.All(cases, sample =>
        {
            Assert.Equal(HttpColdMissBenchmarks.InvocationsPerIteration, sample.Job.Run.InvocationCount);
            Assert.Equal(nameof(HttpColdMissBenchmarks.ResetBatch), sample.Descriptor.IterationSetupMethod?.Name);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HttpColdPublication_EveryOperationLoads_AndIterationResetReleasesResidents(bool listening, bool afterInvalidation)
    {
        var benchmark = new HttpColdMissBenchmarks { Listening = listening, AfterInvalidation = afterInvalidation };
        try
        {
            await benchmark.Setup();
            for (var batch = 0; batch < 2; batch++)
            {
                if (batch != 0) await benchmark.ResetBatch();
                var warm = afterInvalidation ? 7 : 3;
                Assert.Equal(warm, benchmark.OriginLoads);
                var admin = benchmark.Services.GetRequiredService<ICacheAdministrator>();
                Assert.Equal(3, (await admin.GetStatsAsync()).LocalHttpEntries);
                for (var i = 0; i < HttpColdMissBenchmarks.InvocationsPerIteration; i++)
                    Assert.Equal(200, await benchmark.Miss());
                Assert.Equal(warm + HttpColdMissBenchmarks.InvocationsPerIteration, benchmark.OriginLoads);
                var stats = await admin.GetStatsAsync();
                Assert.Equal(HttpColdMissBenchmarks.InvocationsPerIteration + 3, stats.LocalHttpEntries);
                Assert.InRange(stats.LocalHttpBytes, 1, 100L * 1024 * 1024);
            }
        }
        finally { benchmark.Cleanup(); }
    }

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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpHitScenarios_AfterInvalidationStillHitTheirRepopulatedNamespaces(bool listening)
    {
        var benchmark = new HttpResponsePathBenchmarks { Listening = listening, AfterInvalidation = true };
        try
        {
            await benchmark.Setup();
            Assert.Equal(7, benchmark.OriginLoads);
            Assert.Equal(200, await benchmark.FreshHit());
            Assert.Equal(200, await benchmark.VariantHit());
            Assert.Equal(7, benchmark.OriginLoads);
        }
        finally { benchmark.Cleanup(); }
    }

}
