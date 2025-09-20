using BenchmarkDotNet.Running;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
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

        var config = CreateBenchmarkConfig();

        switch (args[0].ToLowerInvariant())
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
                
            case "all":
                RunAllBenchmarks(config);
                break;
                
            default:
                Console.WriteLine($"Unknown benchmark category: {args[0]}");
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
        Console.WriteLine("  providers    - Compare different cache providers (InMemory, Redis, Hybrid)");
        Console.WriteLine("  concurrent   - Concurrent access and scalability tests");
        Console.WriteLine("  memory       - Memory usage and GC pressure analysis");
        Console.WriteLine("  realworld    - Real-world application scenarios");
        Console.WriteLine("  generic      - Generic interface performance");
        Console.WriteLine("  serialization - Serialization performance comparison");
        Console.WriteLine("  all          - Run all benchmark categories");
        Console.WriteLine("");
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- basic");
        Console.WriteLine("  dotnet run -- providers");
        Console.WriteLine("  dotnet run -- all");
    }

    private static IConfig CreateBenchmarkConfig()
    {
        // Check if we're in quick mode for development
        var isQuickMode = Environment.GetEnvironmentVariable("BENCHMARK_QUICK") == "true";

        var job = Job.Default
            .WithPlatform(Platform.X64)
            .WithGcServer(true)
            .WithGcConcurrent(true)
            .WithGcRetainVm(true)
            .WithToolchain(InProcessEmitToolchain.Instance);

        if (isQuickMode)
        {
            // Quick mode: fewer warmup iterations and actual runs
            job = job
                .WithWarmupCount(1)
                .WithIterationCount(3)
                .WithMaxRelativeError(0.10); // Allow higher variance for speed
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