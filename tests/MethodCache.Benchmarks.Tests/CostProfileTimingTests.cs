using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.Results;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class CostProfileTimingTests
{
    [Theory]
    [InlineData(nameof(BatchedWork.Single))]
    [InlineData(nameof(BatchedWork.Sixteen))]
    [InlineData(nameof(BatchedWork.SixtyFour))]
    public void Record_BatchedReport_UsesTimePerOperationExactlyOnce(string method)
    {
        var report = Report(method, actualCount: 12);
        Assert.Equal(1000, report.ResultStatistics!.Median);
        Assert.Equal(1000d, Timing.Record(report)["ns"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Record_IncompleteActualIterations_DoesNotExportAcceptedTime(int actualCount)
    {
        Assert.Null(Timing.Record(Report(nameof(BatchedWork.Single), actualCount))["ns"]);
    }

    private static BenchmarkReport Report(string method, int actualCount)
    {
        var benchmark = BenchmarkConverter.TypeToBenchmarks(typeof(BatchedWork)).BenchmarksCases
            .Single(c => c.Descriptor.WorkloadMethod.Name == method);
        var operations = 128 * benchmark.Descriptor.OperationsPerInvoke;
        var lines = Enumerable.Range(1, actualCount).Select(i =>
                new Measurement(1, IterationMode.Workload, IterationStage.Actual, i, operations, operations * 1000).ToString())
            .Append(new Measurement(1, IterationMode.Workload, IterationStage.Result, 1, operations, operations * 1000).ToString())
            .ToArray();
        var execution = new ExecuteResult(true, 0, 1, lines, [], [], 1);
        return new BenchmarkReport(true, benchmark, null!, null!, [execution], []);
    }

    public class BatchedWork
    {
        [Benchmark] public int Single() => 1;
        [Benchmark(OperationsPerInvoke = 16)] public int Sixteen() => 16;
        [Benchmark(OperationsPerInvoke = 64)] public int SixtyFour() => 64;
    }
}
