from pathlib import Path
import argparse,hashlib,json,re,subprocess
from generated_build import add_build_validation
p=argparse.ArgumentParser();p.add_argument('--runtime-root',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--workloads-root',type=Path,required=True);a=p.parse_args()
root=a.runtime_root.resolve();source=a.workloads_root.resolve();out=a.output.resolve();out.mkdir(exist_ok=True,parents=True)
files=['Microbenchmarks/PrefixHitPathBenchmarks.cs','Microbenchmarks/GeneratedPrefixHitPathBenchmarks.cs','Microbenchmarks/KeyHashLookupBenchmarks.cs','Microbenchmarks/MemoryCacheLookupReferenceBenchmarks.cs']
checksums={}
for file in files:
 data=(source/'benchmarks/MethodCache.Benchmarks'/file).read_bytes();(out/Path(file).name).write_bytes(data);checksums[file]=hashlib.sha256(data).hexdigest()
core=root/'src/MethodCache.Core/MethodCache.Core.csproj';generator=root/'src/MethodCache.SourceGenerator/MethodCache.SourceGenerator.csproj'
keys=root/'src/MethodCache.Core/Runtime/Keys'
sip=(keys/'SipHashState.cs').is_file();marvin=(keys/'MarvinState.cs').is_file()
if sip and marvin:raise SystemExit('Ambiguous prefix hash implementations in the runtime checkout.')
builder=(root/'src/MethodCache.Core/Generated/KeyBuilder.cs').read_text()
constructor=bool(re.search(r'public\s+KeyBuilder\s*\(\s*MethodCall\s+\w+\s*,\s*Span<char>\s+\w+\s*\)',builder))
features=[]
if sip or marvin:features.append('METHODCACHE_PREFIX_HASH')
if sip:features.append('METHODCACHE_SIPHASH')
if constructor:features.append('METHODCACHE_KEY_BUILDER_CONSTRUCTOR')
symbol=''.join(';'+feature for feature in features)
capabilities={'prefix_hash':sip or marvin,'hash_algorithm':'SipHash-1-3' if sip else 'Marvin32' if marvin else 'runtime string hash','in_place_key_builder':constructor}
(out/'MethodCache.Benchmarks.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>MethodCache.Benchmarks</AssemblyName>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>latest</LangVersion>
<ServerGarbageCollection>false</ServerGarbageCollection><ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<DefineConstants>$(DefineConstants){symbol}</DefineConstants></PropertyGroup><ItemGroup>
<PackageReference Include="BenchmarkDotNet" Version="0.15.4"/><PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="9.0.9"/>
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.9"/>
<ProjectReference Include="{core}"/><ProjectReference Include="{generator}" OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
</ItemGroup></Project>''')
(out/'Program.cs').write_text('''using BenchmarkDotNet.Configs;
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
''')
add_build_validation(out/'Program.cs')
(out/'source-manifest.json').write_text(json.dumps({'runtime_root':str(root),'runtime_sha':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'workload_sha':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip(),'source_sha256':checksums,'harness_program_sha256':hashlib.sha256((out/'Program.cs').read_bytes()).hexdigest(),'compiled_capabilities':capabilities,'statistics':'PR5 and later have no independent statistics toggle; facade and generated-interface cases measure both MeterListener states. No ineffective flag is varied.'},indent=2))
print(out/'MethodCache.Benchmarks.csproj')
