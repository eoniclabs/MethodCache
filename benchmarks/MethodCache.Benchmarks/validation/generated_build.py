"""Add a compile-only gate using BDN 0.15.4's actual partitioner, generator and builder."""
from pathlib import Path

USINGS = '''using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.CsProj;
'''
BUILD_ONLY = '''if (args.FirstOrDefault() == "--build-only")
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
'''

def add_build_validation(program: Path):
    source = program.read_text()
    anchor = 'if (args.FirstOrDefault() == "--workload-inventory")'
    if source.count(anchor) != 1:
        raise ValueError('Expected one inventory entry point in the generated harness')
    program.write_text(USINGS + source.replace(anchor, BUILD_ONLY + anchor))
