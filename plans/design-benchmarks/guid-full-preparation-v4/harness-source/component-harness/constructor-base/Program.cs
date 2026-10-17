using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using MethodCache.Benchmarks.Microbenchmarks;
using MethodCache.Generated;
using System.Text.Json;
var types = new[] { typeof(PrefixHitPathBenchmarks), typeof(GeneratedPrefixHitPathBenchmarks),
    typeof(KeyHashLookupBenchmarks), typeof(MemoryCacheLookupReferenceBenchmarks) };
var config = DefaultConfig.Instance.AddJob(Job.Default.WithGcServer(false).WithGcConcurrent(true));
if (args.FirstOrDefault() == "--workload-inventory")
{
    var patterns = args.Skip(1).ToArray();
    if (patterns.Length == 0) throw new ArgumentException("Supply the timing filters.");
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
if (args.Contains("--verify-components"))
{
    var core = typeof(CachedMethod).Assembly;
    var expectedPrefixHash = core.GetType("MethodCache.Core.Runtime.Keys.PrehashedKey") != null;
    var expectedSipHash = core.GetType("MethodCache.Core.Runtime.Keys.SipHashState") != null;
    var expectedConstructor = typeof(KeyBuilder).GetConstructors().Any(constructor =>
    {
        var parameters = constructor.GetParameters();
        return parameters.Length == 2 && parameters[0].ParameterType == typeof(MethodCall) && parameters[1].ParameterType == typeof(Span<char>);
    });
    if (KeyHashLookupBenchmarks.UsesPrefixHash != expectedPrefixHash || KeyHashLookupBenchmarks.UsesSipHash != expectedSipHash ||
        PrefixHitPathBenchmarks.UsesInPlaceKeyBuilder != expectedConstructor)
        throw new Exception("Compiled component paths do not match the referenced Core implementation.");
    Console.WriteLine($"Verified compiled capabilities: prefix={expectedPrefixHash}, SipHash={expectedSipHash}, in-place builder={expectedConstructor}.");
    foreach (var length in new[] { 4, 80, 256 })
    foreach (var argument in new[] { "Int", "Guid" })
    {
        var lookup = new KeyHashLookupBenchmarks { PrefixCharacters = length, ArgumentKind = argument, SelectPerformanceCores = false };
        lookup.Setup();
        if (lookup.ReadyStringLookup() != 42 || lookup.FullSpanLookup() != 42 || lookup.PrefixLookup() != 42)
            throw new Exception("Component lookup missed.");
    }
    foreach (var length in new[] { 4, 256 })
    foreach (var buffer in new[] { 128, 512 })
    foreach (var listening in new[] { false, true })
    {
        var hit = new PrefixHitPathBenchmarks
        { MethodNameLength = length, BufferCharacters = buffer, Listening = listening, SelectPerformanceCores = false };
        // Preserve a setup exception: cleanup requires a successfully created provider.
        hit.Setup();
        try
        {
            for (var i = 0; i < 100; i++)
                if (hit.SyncHit() != 42 || await hit.TaskHit() != 42 || await hit.ValueTaskHit() != 42)
                    throw new Exception("Facade component missed.");
            var keyLength = hit.KeyBuild();
            if (hit.OriginLoads != 1 || keyLength <= length || (length == 256 && buffer == 128 && keyLength <= buffer))
                throw new Exception("Component setup did not exercise its declared prefix/growth or caching.");
        }
        finally { hit.Cleanup(); }
    }
    var reference = new MemoryCacheLookupReferenceBenchmarks { SelectPerformanceCores = false };
    reference.Setup();
    try
    {
        if (reference.ReadyKeySync() != 42 || reference.BuiltKeySync() != 42 ||
            await reference.ReadyKeyTask() != 42 || await reference.BuiltKeyTask() != 42)
            throw new Exception("Reference cache missed.");
    }
    finally { reference.Cleanup(); }
    foreach (var argument in new[] { PrefixArgumentKind.Int, PrefixArgumentKind.Guid })
    foreach (var longPrefix in new[] { false, true })
    foreach (var listening in new[] { false, true })
    {
        var generated = new GeneratedPrefixHitPathBenchmarks
        { ArgumentKind = argument, LongPrefix = longPrefix, Listening = listening, SelectPerformanceCores = false };
        generated.Setup();
        try
        {
            if ((generated.KeyLength > 256) != longPrefix || generated.OriginLoads != 33)
                throw new Exception("Generated prefix setup did not exercise the declared path.");
            generated.VerifyResidentHits();
            generated.VerifyUniqueMisses();
            if (generated.OriginLoads != 63) throw new Exception("Generated prefix miss guard failed.");
        }
        finally { generated.Cleanup(); }
    }
    Console.WriteLine("Verified all component prefix/growth/listener/cache/reference setup combinations.");
    return;
}
BenchmarkSwitcher.FromTypes(types).Run(args,config);
