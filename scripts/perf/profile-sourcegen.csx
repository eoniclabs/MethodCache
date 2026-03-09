#r "nuget: BenchmarkDotNet, 0.13.12"
#r "/Users/johan/dev/MethodCache/MethodCache.Benchmarks/bin/Release/net9.0/MethodCache.Benchmarks.dll"

using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Exporters;
using MethodCache.Benchmarks.Microbenchmarks;

var isQuickMode = Environment.GetEnvironmentVariable("BENCHMARK_QUICK") == "true";

var job = BenchmarkDotNet.Jobs.Job.Default
    .WithWarmupCount(isQuickMode ? 1 : 3)
    .WithIterationCount(isQuickMode ? 3 : 10);

var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(job)
    .AddExporter(MarkdownExporter.GitHub)
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

BenchmarkRunner.Run<SourceGenSyncPathProfiler>(config);
