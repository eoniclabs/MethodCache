using System.Runtime.CompilerServices;
using MethodCache.Benchmarks.Comparison;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

[Collection("Scenario counters")]
public class GeneratedMissAllocationTests
{
    [Theory]
    [InlineData(false, 1088)]
    [InlineData(true, 1096)]
    public void MemoryOnlyUniqueMisses_DoNotAllocateUnusedBranchClosures(bool valueTask, int budget)
    {
        var benchmark = new GeneratedContractBenchmarks { SelectPerformanceCores = false };
        benchmark.Setup();
        try
        {
            // Assembly scoping adds characters to the one key string allocated on a miss.
            // Charge exactly that string-size difference, preserving the original closure budget.
            var key = string.Concat(MethodCacheKeys.Prefix(typeof(IContractService), valueTask ? "ValueTask" : "Task"), ":", MethodCacheKeys.Part(Guid.Empty));
            var assemblyLength = key.IndexOf(']') + 1;
            static int StringBytes(int length) => (22 + 2 * length + 7) & ~7;
            budget += StringBytes(key.Length) - StringBytes(key.Length - assemblyLength);
            const int count = 10000;
            var before = benchmark.OriginLoads;
            _ = Measure(benchmark, valueTask, count);
            var allocated = Measure(benchmark, valueTask, count);
            Assert.Equal(before + 2 * count, benchmark.OriginLoads);
            // Original cumulative baseline: 1080/1088 B; 8 B/op leaves room for
            // incidental runtime bookkeeping, while either unused 24 B closure fails.
            Assert.True(allocated <= (long)count * budget,
                $"Allocated {allocated / (double)count} B per unique miss; budget is {budget} B.");
        }
        finally { benchmark.Cleanup(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(GeneratedContractBenchmarks benchmark, bool valueTask, int count)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        if (valueTask)
            for (var i = 0; i < count; i++) _ = benchmark.ValueTaskMiss().GetAwaiter().GetResult();
        else
            for (var i = 0; i < count; i++) _ = benchmark.TaskMiss().GetAwaiter().GetResult();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
