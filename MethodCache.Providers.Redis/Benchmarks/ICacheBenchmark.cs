using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Benchmarks
{
    public interface ICacheBenchmark
    {
        Task<BenchmarkResult> RunAsync(BenchmarkOptions options);
        string Name { get; }
        string Description { get; }
    }

    public class BenchmarkOptions
    {
        public int Iterations { get; set; } = 10000;
        public int KeySize { get; set; } = 50;
        public int ValueSize { get; set; } = 1024;
        public int ConcurrentOperations { get; set; } = 10;
        public int WarmupIterations { get; set; } = 1000;
        public TimeSpan TestDuration { get; set; } = TimeSpan.FromSeconds(30);
        public bool UseRandomKeys { get; set; } = true;
        public bool UseRandomValues { get; set; } = true;
        public double HitRatio { get; set; } = 0.8; // For read benchmarks
        public string[] Tags { get; set; } = Array.Empty<string>();
        public TimeSpan? DefaultExpiry { get; set; }
    }

    public class BenchmarkResult
    {
        public string BenchmarkName { get; set; } = string.Empty;
        public long TotalOperations { get; set; }
        public TimeSpan Duration { get; set; }
        public double OperationsPerSecond => Duration.TotalSeconds > 0 ? TotalOperations / Duration.TotalSeconds : 0;
        public TimeSpan AverageLatency => TotalOperations > 0 ? TimeSpan.FromTicks(Duration.Ticks / TotalOperations) : TimeSpan.Zero;
        public TimeSpan MinLatency { get; set; }
        public TimeSpan MaxLatency { get; set; }
        public TimeSpan P50Latency { get; set; }
        public TimeSpan P95Latency { get; set; }
        public TimeSpan P99Latency { get; set; }
        public long MemoryUsedBytes { get; set; }
        public long Errors { get; set; }
        public Dictionary<string, object> Metrics { get; set; } = new();
        public List<string> ErrorMessages { get; set; } = new();
    }

    public class BenchmarkSuite
    {
        private readonly List<ICacheBenchmark> _benchmarks = new();

        public BenchmarkSuite AddBenchmark(ICacheBenchmark benchmark)
        {
            _benchmarks.Add(benchmark);
            return this;
        }

        public async Task<BenchmarkSuiteResult> RunAllAsync(BenchmarkOptions options)
        {
            var results = new List<BenchmarkResult>();
            var startTime = DateTime.UtcNow;

            foreach (var benchmark in _benchmarks)
            {
                try
                {
                    Console.WriteLine($"Running benchmark: {benchmark.Name}");
                    var result = await benchmark.RunAsync(options);
                    results.Add(result);
                    
                    Console.WriteLine($"  {result.OperationsPerSecond:N0} ops/sec, " +
                                      $"avg: {result.AverageLatency.TotalMicroseconds:F2}μs, " +
                                      $"errors: {result.Errors}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR: {ex.Message}");
                    results.Add(new BenchmarkResult
                    {
                        BenchmarkName = benchmark.Name,
                        Errors = 1,
                        ErrorMessages = new List<string> { ex.Message }
                    });
                }
            }

            return new BenchmarkSuiteResult
            {
                Results = results,
                TotalDuration = DateTime.UtcNow - startTime,
                Options = options
            };
        }
    }

    public class BenchmarkSuiteResult
    {
        public List<BenchmarkResult> Results { get; set; } = new();
        public TimeSpan TotalDuration { get; set; }
        public BenchmarkOptions Options { get; set; } = new();

        public void PrintSummary()
        {
            Console.WriteLine();
            Console.WriteLine("=== BENCHMARK SUMMARY ===");
            Console.WriteLine($"Total Duration: {TotalDuration.TotalSeconds:F2}s");
            Console.WriteLine($"Iterations: {Options.Iterations:N0}");
            Console.WriteLine($"Concurrent Operations: {Options.ConcurrentOperations}");
            Console.WriteLine();

            Console.WriteLine($"{"Benchmark",-30} {"Ops/Sec",-12} {"Avg (μs)",-12} {"P95 (μs)",-12} {"P99 (μs)",-12} {"Errors",-10}");
            Console.WriteLine(new string('-', 90));

            foreach (var result in Results)
            {
                Console.WriteLine($"{result.BenchmarkName,-30} " +
                                  $"{result.OperationsPerSecond,-12:N0} " +
                                  $"{result.AverageLatency.TotalMicroseconds,-12:F2} " +
                                  $"{result.P95Latency.TotalMicroseconds,-12:F2} " +
                                  $"{result.P99Latency.TotalMicroseconds,-12:F2} " +
                                  $"{result.Errors,-10}");
            }
        }
    }
}