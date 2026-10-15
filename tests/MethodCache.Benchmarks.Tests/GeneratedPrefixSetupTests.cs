using System.Reflection;
using MethodCache.Benchmarks.Microbenchmarks;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class GeneratedPrefixSetupTests
{
    [Theory]
    [InlineData(PrefixArgumentKind.Int, false, false)]
    [InlineData(PrefixArgumentKind.Int, false, true)]
    [InlineData(PrefixArgumentKind.Int, true, false)]
    [InlineData(PrefixArgumentKind.Int, true, true)]
    [InlineData(PrefixArgumentKind.Guid, false, false)]
    [InlineData(PrefixArgumentKind.Guid, false, true)]
    [InlineData(PrefixArgumentKind.Guid, true, false)]
    [InlineData(PrefixArgumentKind.Guid, true, true)]
    public void GeneratedPrefix_AllReturnShapesHit_AndEachMissLoads(PrefixArgumentKind argumentKind, bool longPrefix, bool listening)
    {
        var benchmark = new GeneratedPrefixHitPathBenchmarks
        { ArgumentKind = argumentKind, LongPrefix = longPrefix, Listening = listening, SelectPerformanceCores = false };
        benchmark.Setup();
        try
        {
            Assert.Equal(longPrefix, benchmark.KeyLength > 256);
            Assert.Equal(33, benchmark.OriginLoads);
            benchmark.VerifyResidentHits();
            Assert.Equal(33, benchmark.OriginLoads);
            benchmark.VerifyUniqueMisses();
            Assert.Equal(63, benchmark.OriginLoads);
        }
        finally { benchmark.Cleanup(); }
    }

    [Theory]
    [InlineData(PrefixArgumentKind.Int, false)]
    [InlineData(PrefixArgumentKind.Int, true)]
    [InlineData(PrefixArgumentKind.Guid, false)]
    [InlineData(PrefixArgumentKind.Guid, true)]
    public void GeneratedPrefix_ResidentGuardRejectsUndecoratedService(PrefixArgumentKind argumentKind, bool longPrefix)
    {
        var benchmark = new GeneratedPrefixHitPathBenchmarks
        { ArgumentKind = argumentKind, LongPrefix = longPrefix, SelectPerformanceCores = false };
        benchmark.Setup();
        try
        {
            typeof(GeneratedPrefixHitPathBenchmarks).GetField(longPrefix ? "_long" : "_short", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(benchmark, benchmark.OriginService);
            Assert.Throws<InvalidOperationException>(() => benchmark.VerifyResidentHits());
        }
        finally { benchmark.Cleanup(); }
    }
}
