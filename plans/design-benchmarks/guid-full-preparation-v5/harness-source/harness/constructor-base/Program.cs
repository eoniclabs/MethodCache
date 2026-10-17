using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using MethodCache.Benchmarks.Comparison;
using MethodCache.Benchmarks.Microbenchmarks;
using System.Text.Json;
var types = new[] { typeof(GeneratedContractBenchmarks), typeof(HttpResponsePathBenchmarks),
    typeof(HttpColdMissBenchmarks), typeof(DesignMemoryWorkloadBenchmarks), typeof(DesignL2WorkloadBenchmarks), typeof(DesignHttpWorkloadBenchmarks) };
var config = DefaultConfig.Instance.AddJob(Job.Default.WithGcServer(false).WithGcConcurrent(true));
if (args.Contains("--verify-generated"))
{
    var benchmark = new GeneratedContractBenchmarks { SelectPerformanceCores = false };
    benchmark.Setup();
    try
    {
        benchmark.VerifyResidentHits();
        benchmark.VerifyUniqueMisses();
        if (benchmark.OriginLoads != 38) throw new Exception("Generated workload origin counts differ.");
        Console.WriteLine("Verified eight resident generated hit shapes and thirty distinct-key misses.");
    }
    finally { benchmark.Cleanup(); }
    return;
}
if (args.FirstOrDefault() == "--build-only")
{
    // Compile BDN's generated program as well as the harness itself, without executing benchmarks.
    var patterns = args.Skip(1).ToArray();
    if (patterns.Length == 0) throw new ArgumentException("Supply the timing filters.");
    var filter = new GlobFilter(patterns);
    var runs = new List<BenchmarkRunInfo>();
    foreach (var type in types)
    {
        using var run = BenchmarkConverter.TypeToBenchmarks(type, config);
        var selected = run.BenchmarksCases.Where(filter.Predicate).ToArray();
        if (selected.Length != 0) runs.Add(new BenchmarkRunInfo(selected, type, run.Config));
    }
    if (runs.Count == 0) throw new InvalidOperationException("No matching benchmarks.");
    var resolver = new CompositeResolver(EnvironmentResolver.Instance, InfrastructureResolver.Instance);
    var toolchain = CsProjCoreToolchain.NetCoreApp10_0;
    try
    {
        foreach (var partition in BenchmarkPartitioner.CreateForBuild(runs.ToArray(), resolver))
        {
            var generated = toolchain.Generator.GenerateProject(partition, ConsoleLogger.Default,
                Path.Combine(Environment.CurrentDirectory, "bdn-build-validation"));
            if (!generated.IsGenerateSuccess) throw new InvalidOperationException("BDN generation failed.", generated.GenerateException);
            var built = toolchain.Builder.Build(generated, partition, ConsoleLogger.Default);
            if (!built.IsBuildSuccess) throw new InvalidOperationException("BDN compilation failed: " + built.ErrorMessage);
        }
        Console.WriteLine($"Compiled generated BDN programs for {runs.Sum(run => run.BenchmarksCases.Length)} cases; no benchmarks executed.");
    }
    finally { foreach (var run in runs) run.Dispose(); }
    return;
}
if (args.FirstOrDefault() == "--workload-inventory")
{
    var patterns = args.Skip(1).ToArray();
    if (patterns.Length == 0) throw new ArgumentException("Supply the same filters as the timing launch.");
    var filter = new GlobFilter(patterns);
    var cases = new List<object>();
    foreach (var type in types)
    {
        using var run = BenchmarkConverter.TypeToBenchmarks(type, config);
        foreach (var benchmark in run.BenchmarksCases.Where(filter.Predicate))
            cases.Add(new { full_name = FullNameProvider.GetBenchmarkName(benchmark),
                report_type = benchmark.Descriptor.Type.FullName });
    }
    Console.WriteLine(JsonSerializer.Serialize(cases));
    return;
}
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
if (args.Contains("--verify-extra-local"))
{
    using var definition = BenchmarkConverter.TypeToBenchmarks(typeof(HttpColdMissBenchmarks), config);
    if (definition.BenchmarksCases.Length != 4 || definition.BenchmarksCases.Any(c =>
        c.Job.Run.InvocationCount != HttpColdMissBenchmarks.InvocationsPerIteration || c.Job.Run.UnrollFactor != 1 ||
        c.Descriptor.IterationSetupMethod?.Name != "ResetBatch"))
        throw new Exception("Bounded HTTP publication job definition changed.");
    foreach (var listening in new[] { false, true })
    foreach (var invalidated in new[] { false, true })
    {
        var cold = new HttpColdMissBenchmarks { Listening = listening, AfterInvalidation = invalidated };
        try
        {
            await cold.Setup();
            for (var batch = 0; batch < 2; batch++)
            {
                if (batch != 0) cold.ResetBatch();
                var initial = invalidated ? 7 : 3;
                if (cold.OriginLoads != initial) throw new Exception("HTTP state leaked across cold batches.");
                for (var i = 0; i < HttpColdMissBenchmarks.InvocationsPerIteration; i++)
                    if (await cold.Miss() != 200) throw new Exception("Cold publication returned the wrong response.");
                if (cold.OriginLoads != initial + HttpColdMissBenchmarks.InvocationsPerIteration)
                    throw new Exception("Cold publication did not load origin every time.");
            }
        }
        finally { cold.Cleanup(); }
        var hit = new HttpResponsePathBenchmarks { Listening = listening, AfterInvalidation = invalidated };
        try
        {
            await hit.Setup();
            var before = hit.OriginLoads;
            if (await hit.FreshHit() != 200 || await hit.VariantHit() != 200 || hit.OriginLoads != before)
                throw new Exception("HTTP resident hit called origin.");
            if (await hit.Revalidated() != 200 || hit.OriginLoads != before + 1)
                throw new Exception("HTTP revalidation did not call origin.");
        }
        finally { hit.Cleanup(); }
    }
    foreach (var listening in new[] { false, true })
    foreach (var weighted in new[] { false, true })
    {
        var memory = new DesignMemoryWorkloadBenchmarks
        { Listening = listening, Weighted = weighted, SelectPerformanceCores = false };
        try
        {
            await memory.Setup();
            if (await memory.Hit() != 42) throw new Exception("Memory hit failed.");
            await memory.ContendedHits();
            if (await memory.SingleFlightBatch() != 168 || memory.OriginLoads != 1)
                throw new Exception("Single-flight did not share one origin.");
            if (await memory.EvictionPressure() != 42) throw new Exception("Pressure call failed.");
        }
        finally { memory.Cleanup(); }
    }
    foreach (var cookies in new[] { false, true })
    {
        var http = new DesignHttpWorkloadBenchmarks
        { Backend = "Local", KeepCookies = cookies, SelectPerformanceCores = false };
        try
        {
            await http.Setup();
            var before = http.OriginLoads;
            if (await http.Fresh() != 200 || http.OriginLoads != before + (cookies ? 1 : 0))
                throw new Exception("Local HTTP caching/bypass did not match its declaration.");
            if (await http.Miss() != 200 || http.OriginLoads != before + (cookies ? 2 : 1))
                throw new Exception("Local HTTP miss did not load.");
        }
        finally { await http.Cleanup(); }
    }
    Console.WriteLine("Verified cold publication, HTTP hit/revalidation, weighted/listener contention, single-flight and local cookie modes.");
    return;
}
BenchmarkSwitcher.FromTypes(types).Run(args, config);
