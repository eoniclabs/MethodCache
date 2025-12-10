using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Json;
using MethodCache.Benchmarks.Core;
using MethodCache.Benchmarks.Scenarios;

namespace MethodCache.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        Console.WriteLine("MethodCache Performance Benchmarks");
        Console.WriteLine("===================================");
        
        if (args.Length == 0)
        {
            ShowHelp();
            return;
        }

        var quickRequested = false;
        var cleanedArgs = new List<string>(args.Length);

        foreach (var arg in args)
        {
            if (string.Equals(arg, "--quick", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-q", StringComparison.OrdinalIgnoreCase))
            {
                quickRequested = true;
            }
            else
            {
                cleanedArgs.Add(arg);
            }
        }

        if (cleanedArgs.Count == 0)
        {
            ShowHelp();
            return;
        }

        var config = CreateBenchmarkConfig(quickRequested);

        switch (cleanedArgs[0].ToLowerInvariant())
        {
            case "basic":
                BenchmarkRunner.Run<BasicCachingBenchmarks>(config);
                break;

            case "providers":
                BenchmarkRunner.Run<CacheProviderComparisonBenchmarks>(config);
                break;

            case "concurrent":
                BenchmarkRunner.Run<ConcurrentAccessBenchmarks>(config);
                break;

            case "memory":
                BenchmarkRunner.Run<MemoryUsageBenchmarks>(config);
                break;

            case "realworld":
                BenchmarkRunner.Run<RealWorldScenarioBenchmarks>(config);
                break;

            case "generic":
                BenchmarkRunner.Run<GenericInterfaceBenchmarks>(config);
                break;

            case "serialization":
                BenchmarkRunner.Run<SerializationBenchmarks>(config);
                break;

            case "quick":
                BenchmarkRunner.Run<QuickCachingBenchmarks>(config);
                break;

            case "baseline":
                BenchmarkRunner.Run<BaselineComparisonBenchmarks>(config);
                break;

            case "comparison":
                var comparisonArgs = cleanedArgs.Skip(1).ToArray();
                if (comparisonArgs.Length > 0)
                {
                    BenchmarkSwitcher.FromTypes(new[] { typeof(Comparison.UnifiedCacheComparisonBenchmarks) })
                        .Run(comparisonArgs, config);
                }
                else
                {
                    BenchmarkRunner.Run<Comparison.UnifiedCacheComparisonBenchmarks>(config);
                }
                break;

            case "quickcompare":
                BenchmarkRunner.Run<Comparison.QuickMethodCacheTest>(config);
                break;

            case "profile":
                BenchmarkRunner.Run<Microbenchmarks.SourceGenSyncPathProfiler>(config);
                break;

            case "memoryproviders":
                BenchmarkRunner.Run<Microbenchmarks.MemoryProviderComparisonBenchmarks>(config);
                break;

            case "all":
                RunAllBenchmarks(config);
                break;

            default:
                Console.WriteLine($"Unknown benchmark category: {cleanedArgs[0]}");
                ShowHelp();
                break;
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine("Usage: MethodCache.Benchmarks <category>");
        Console.WriteLine("");
        Console.WriteLine("Available categories:");
        Console.WriteLine("  basic        - Basic caching operations (hit/miss, different data sizes)");
        Console.WriteLine("  baseline     - Compare with Microsoft.Extensions.Caching.Memory and LazyCache");
        Console.WriteLine("  comparison   - Unified comparison: MethodCache vs FusionCache vs EasyCaching vs LazyCache");
        Console.WriteLine("  providers    - Compare different cache providers (InMemory, Redis, Hybrid)");
        Console.WriteLine("  concurrent   - Concurrent access and scalability tests");
        Console.WriteLine("  memory       - Memory usage and GC pressure analysis");
        Console.WriteLine("  realworld    - Real-world application scenarios");
        Console.WriteLine("  generic      - Generic interface performance");
        Console.WriteLine("  serialization - Serialization performance comparison");
        Console.WriteLine("  quick        - Quick benchmarks for development (minimal parameters)");
        Console.WriteLine("  memoryproviders - Compare Standard vs Advanced memory providers directly");
        Console.WriteLine("  all          - Run all benchmark categories");
        Console.WriteLine("");
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- basic");
        Console.WriteLine("  dotnet run -- baseline");
        Console.WriteLine("  dotnet run -- comparison");
        Console.WriteLine("  dotnet run -- providers");
        Console.WriteLine("  dotnet run -- all");
        Console.WriteLine("");
        Console.WriteLine("Options:");
        Console.WriteLine("  --quick, -q  Run using the lightweight benchmark job (same as BENCHMARK_QUICK=true)");
    }

    private static IConfig CreateBenchmarkConfig(bool quickRequested)
    {
        // Check if we're in quick mode for development
        var isQuickMode = quickRequested || Environment.GetEnvironmentVariable("BENCHMARK_QUICK") == "true";

        var job = Job.Default
            .WithPlatform(Platform.AnyCpu) // Auto-detect platform (ARM on Mac, x64 on Windows)
            .WithGcServer(true)
            .WithGcConcurrent(true)
            .WithGcRetainVm(true)
            .WithInvocationCount(32)
            .WithUnrollFactor(8);

        if (isQuickMode)
        {
            // Quick mode: fewer warmup iterations and actual runs
            job = job
                .WithWarmupCount(1)
                .WithIterationCount(3)
                .WithMaxRelativeError(0.10); // Allow higher variance for speed

            // Only use the quick job in quick mode
            return ManualConfig.Create(DefaultConfig.Instance)
                .AddJob(job)
                .AddExporter(JsonExporter.Full)
                .AddExporter(MarkdownExporter.GitHub)
                .WithOptions(ConfigOptions.DisableOptimizationsValidator);
        }

        return ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(job)
            .AddExporter(JsonExporter.Full)
            .AddExporter(MarkdownExporter.GitHub)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }

    private static void RunAllBenchmarks(IConfig config)
    {
        Console.WriteLine("Running all benchmark categories...");
        Console.WriteLine("");

        var benchmarkTypes = new[]
        {
            typeof(BasicCachingBenchmarks),
            typeof(CacheProviderComparisonBenchmarks),
            typeof(ConcurrentAccessBenchmarks),
            typeof(MemoryUsageBenchmarks),
            typeof(RealWorldScenarioBenchmarks),
            typeof(GenericInterfaceBenchmarks),
            typeof(SerializationBenchmarks)
        };

        foreach (var benchmarkType in benchmarkTypes)
        {
            Console.WriteLine($"Running {benchmarkType.Name}...");
            BenchmarkRunner.Run(benchmarkType, config);
            Console.WriteLine("");
        }
    }
}
