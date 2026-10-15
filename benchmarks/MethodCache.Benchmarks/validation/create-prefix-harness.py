from pathlib import Path
import argparse,hashlib,json,subprocess
p=argparse.ArgumentParser();p.add_argument('--runtime-root',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--workloads-root',type=Path,required=True);a=p.parse_args()
root=a.runtime_root.resolve();source=a.workloads_root.resolve();out=a.output.resolve();out.mkdir(exist_ok=True,parents=True)
files=['Microbenchmarks/PrefixHitPathBenchmarks.cs','Microbenchmarks/KeyHashLookupBenchmarks.cs','Microbenchmarks/MemoryCacheLookupReferenceBenchmarks.cs']
checksums={}
for file in files:
 data=(source/'benchmarks/MethodCache.Benchmarks'/file).read_bytes();(out/Path(file).name).write_bytes(data);checksums[file]=hashlib.sha256(data).hexdigest()
core=root/'src/MethodCache.Core/MethodCache.Core.csproj';generator=root/'src/MethodCache.SourceGenerator/MethodCache.SourceGenerator.csproj'
symbol=';METHODCACHE_PREFIX_HASH' if (root/'src/MethodCache.Core/Runtime/Keys/MarvinState.cs').exists() else ''
(out/'PrefixValidation.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>MethodCache.Benchmarks</AssemblyName>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>latest</LangVersion>
<ServerGarbageCollection>false</ServerGarbageCollection><ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<DefineConstants>$(DefineConstants){symbol}</DefineConstants></PropertyGroup><ItemGroup>
<PackageReference Include="BenchmarkDotNet" Version="0.15.4"/><PackageReference Include="Microsoft.Extensions.Caching.Memory" Version="9.0.9"/>
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.9"/>
<ProjectReference Include="{core}"/><ProjectReference Include="{generator}" OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
</ItemGroup></Project>''')
(out/'Program.cs').write_text('''using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using MethodCache.Benchmarks.Microbenchmarks;
if (args.Contains("--verify-components"))
{
    foreach (var length in new[] { 4, 80, 256 })
    {
        var lookup = new KeyHashLookupBenchmarks { PrefixCharacters = length, SelectPerformanceCores = false };
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
    Console.WriteLine("Verified all component prefix/growth/listener/cache/reference setup combinations.");
    return;
}
BenchmarkSwitcher.FromTypes(new[] {typeof(PrefixHitPathBenchmarks),typeof(KeyHashLookupBenchmarks),typeof(MemoryCacheLookupReferenceBenchmarks)})
 .Run(args,DefaultConfig.Instance.AddJob(Job.Default.WithGcServer(false).WithGcConcurrent(true)));
''')
(out/'source-manifest.json').write_text(json.dumps({'runtime_root':str(root),'runtime_sha':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),'workload_sha':subprocess.check_output(['git','rev-parse','HEAD'],cwd=source,text=True).strip(),'source_sha256':checksums,'statistics':'PR5 and later have no independent statistics toggle; each full facade/key-build case measures both MeterListener states. No ineffective flag is varied.'},indent=2))
print(out/'PrefixValidation.csproj')
