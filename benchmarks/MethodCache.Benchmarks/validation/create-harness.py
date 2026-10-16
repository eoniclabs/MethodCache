#!/usr/bin/env python3
"""Build the same public workload sources against a selected design-series checkout.

python3 create-harness.py --runtime-root /path/to/checkout --output /tmp/design-harness/pr5
The harness is kept outside the checkout: the primary repository and frozen vendors are never edited.
Then dotnet build <output>/DesignValidation.csproj -c Release; run its DLL with --verify-native before timing.
"""
import argparse
from generated_build import add_build_validation
import hashlib
import json
from pathlib import Path
import subprocess
import xml.sax.saxutils

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--runtime-root', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
source_root = Path(__file__).resolve().parents[3]
runtime_root = args.runtime_root.resolve()
output = args.output.resolve()
if output.is_relative_to(runtime_root):
    parser.error('Keep the harness outside the runtime checkout.')
output.mkdir(parents=True, exist_ok=True)
sources = [
    'Comparison/GeneratedContractBenchmarks.cs',
    'Microbenchmarks/HttpResponsePathBenchmarks.cs',
    'Microbenchmarks/DesignWorkloadBenchmarks.cs',
]
checksums = {}
for source in sources:
    content = (source_root / 'benchmarks/MethodCache.Benchmarks' / source).read_bytes()
    (output / Path(source).name).write_bytes(content)
    checksums[source] = hashlib.sha256(content).hexdigest()
references = []
for project in ['Core', 'Redis', 'SqlServer', 'HttpCaching', 'OpenTelemetry']:
    path = runtime_root / f'src/MethodCache.{project}/MethodCache.{project}.csproj'
    references.append(f'<ProjectReference Include="{xml.sax.saxutils.escape(str(path))}" />')
generator = runtime_root / 'src/MethodCache.SourceGenerator/MethodCache.SourceGenerator.csproj'
references.append(f'<ProjectReference Include="{generator}" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />')
(output / 'DesignValidation.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>latest</LangVersion>
<ServerGarbageCollection>false</ServerGarbageCollection><ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<RestoreEnablePackagePruning>false</RestoreEnablePackagePruning></PropertyGroup>
<ItemGroup><PackageReference Include="BenchmarkDotNet" Version="0.15.4" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.9" />
''' + '\n'.join(references) + '</ItemGroup></Project>\n')
(output / 'Program.cs').write_text('''using BenchmarkDotNet.Configs;
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
BenchmarkSwitcher.FromTypes(types).Run(args, config);
''')
add_build_validation(output / 'Program.cs')
metadata = {
    'runtime_root': str(runtime_root),
    'runtime_sha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=runtime_root, text=True).strip(),
    'workloads_sha': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source_root, text=True).strip(),
    'source_sha256': checksums,
    'harness_program_sha256': hashlib.sha256((output / 'Program.cs').read_bytes()).hexdigest(),
    'roundtrip_before_pr4': 'Feature absent; both labels retain the existing baseline behavior; report separately.',
    'keepcookies_before_pr6': 'HttpClient without AddHttpCaching, preserving cookie behavior; new workload, not a previous cache feature.',
}
(output / 'source-manifest.json').write_text(json.dumps(metadata, indent=2) + '\n')
print(output / 'DesignValidation.csproj')
