using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using MethodCache.Benchmarks.Comparison;
using MethodCache.Benchmarks.Microbenchmarks;
if (args.Contains("--verify-native"))
{
    foreach (var backend in new[] { "Redis", "SqlServer" })
    {
        foreach (var roundTrip in new[] { false, true })
        {
            var bench = new DesignL2WorkloadBenchmarks { Backend = backend, RoundTrip = roundTrip, SelectPerformanceCores = false };
            try
            {
                await bench.Setup();
                if (await bench.Read() != 42 || bench.OriginLoads != 1) throw new Exception("L2 read bypassed.");
                if (await bench.Write() != 42 || bench.OriginLoads != 2) throw new Exception("L2 write did not load.");
                Console.WriteLine($"Verified {backend} method read/write, roundTrip={roundTrip}");
            }
            finally { await bench.Cleanup(); }
        }
        foreach (var cookies in new[] { false, true })
        {
            var bench = new DesignHttpWorkloadBenchmarks { Backend = backend, KeepCookies = cookies, SelectPerformanceCores = false };
            try
            {
                await bench.Setup();
                var before = bench.OriginLoads;
                if (await bench.Fresh() != 200 || bench.OriginLoads != before + (cookies ? 1 : 0)) throw new Exception("HTTP path unproven.");
                if (await bench.Miss() != 200 || bench.OriginLoads != before + (cookies ? 2 : 1)) throw new Exception("HTTP miss did not load.");
                Console.WriteLine($"Verified {backend} HTTP hit/miss, KeepCookies={cookies}");
            }
            finally { await bench.Cleanup(); }
        }
    }
    return;
}
BenchmarkSwitcher.FromTypes(new[] { typeof(GeneratedContractBenchmarks), typeof(HttpResponsePathBenchmarks),
    typeof(DesignMemoryWorkloadBenchmarks), typeof(DesignL2WorkloadBenchmarks), typeof(DesignHttpWorkloadBenchmarks) })
    .Run(args, DefaultConfig.Instance.AddJob(Job.Default.WithGcServer(false).WithGcConcurrent(true)));
