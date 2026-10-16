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

if (args.Contains("--verify-http"))
{
    foreach (var listening in new[] { false, true })
    foreach (var invalidated in new[] { false, true })
    {
        var bench = new HttpResponsePathBenchmarks { Listening = listening, AfterInvalidation = invalidated };
        try
        {
            await bench.Setup();
            var initial = invalidated ? 7 : 3;
            if (bench.OriginLoads != initial) throw new Exception("Incorrect initial HTTP origin count.");
            for (var i = 0; i < 100; i++)
            {
                var before = bench.OriginLoads;
                if (await bench.FreshHit() != 200 || await bench.VariantHit() != 200 || bench.OriginLoads != before)
                    throw new Exception("HTTP resident path bypassed.");
                if (await bench.Revalidated() != 200 || bench.OriginLoads != before + 1)
                    throw new Exception("HTTP conditional revalidation bypassed.");
            }
            Console.WriteLine($"Verified 100 resident/304 operations, listener={listening}, invalidated={invalidated}");
        }
        finally { bench.Cleanup(); }
    }
    return;
}

BenchmarkSwitcher.FromTypes(new[] { typeof(GeneratedContractBenchmarks), typeof(HttpResponsePathBenchmarks),
    typeof(DesignMemoryWorkloadBenchmarks), typeof(DesignL2WorkloadBenchmarks), typeof(DesignHttpWorkloadBenchmarks) })
    .Run(args, DefaultConfig.Instance.AddJob(Job.Default.WithGcServer(false).WithGcConcurrent(true)));
