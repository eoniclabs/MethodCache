using MethodCache.Benchmarks.Comparison;
using MethodCache.Benchmarks.Microbenchmarks;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class InProcessSchedulingTests
{
    [Theory]
    [InlineData("contract", ThreadPriority.Normal)]
    [InlineData("contract", ThreadPriority.Highest)]
    [InlineData("design", ThreadPriority.Normal)]
    [InlineData("design", ThreadPriority.Highest)]
    [InlineData("prefix", ThreadPriority.Normal)]
    [InlineData("prefix", ThreadPriority.Highest)]
    public void Setup_WithBenchmarkDotNetThreadPriority_PreservesUsableScheduling(string scenario, ThreadPriority priority)
    {
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            failure = Record.Exception(() =>
            {
                var before = Thread.CurrentThread.Priority;
                switch (scenario)
                {
                    case "contract":
                        var benchmark = new GeneratedContractBenchmarks();
                        benchmark.Setup();
                        try { benchmark.VerifyResidentHits(); }
                        finally { benchmark.Cleanup(); }
                        break;
                    case "design":
                        DesignWorkloadSetup.PreferPerformanceCores();
                        break;
                    default:
                        PrefixBenchmarkSetup.PreferPerformanceCores();
                        break;
                }
                Assert.Equal(before, Thread.CurrentThread.Priority);
            });
        })
        {
            IsBackground = true
        };
        // BDN's in-process executor sets Highest before GlobalSetup. Explicit pthread
        // scheduling and a later QoS-class assignment conflict on macOS.
        if (OperatingSystem.IsMacOS() && priority != ThreadPriority.Normal) worker.Priority = priority;
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "Benchmark setup did not complete.");
        Assert.Null(failure);
    }
}
