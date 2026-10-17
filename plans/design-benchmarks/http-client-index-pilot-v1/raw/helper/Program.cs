using System.Reflection;
using System.Text.Json;
using System.Runtime;
using System.Diagnostics;
using System.Security.Cryptography;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using MethodCache.Benchmarks.Microbenchmarks;

var requested = args[0];
var warmups = int.Parse(args[1]);
if (warmups is not (4 or 32)) throw new InvalidOperationException("Only predeclared warmup settings are allowed.");
var types = (Type[])typeof(HttpColdMissBenchmarks).Assembly.GetType("Program")!
    .GetField("Types", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
var job = Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithWarmupCount(warmups).WithIterationCount(12)
    .WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(150));
var config = ManualConfig.CreateEmpty().AddJob(job).AddLogger(NullLogger.Instance).WithOptions(ConfigOptions.DisableLogFile);
var matched = 0;
foreach (var type in types)
{
    var all = BenchmarkConverter.TypeToBenchmarks(type, config);
    var cases = all.BenchmarksCases.Where(c => Name(c) == requested).ToArray();
    if (cases.Length == 0) continue;
    matched += cases.Length;
    if (cases.Length != 1) throw new InvalidOperationException("Ambiguous case.");
    var summary = BenchmarkRunner.Run(new BenchmarkRunInfo(cases, type, all.Config));
    var report = summary.Reports.Single();
    var actual = report.AllMeasurements.Where(m => m.IterationMode == IterationMode.Workload && m.IterationStage == IterationStage.Actual).ToArray();
    var warm = report.AllMeasurements.Where(m => m.IterationMode == IterationMode.Workload && m.IterationStage == IterationStage.Warmup).ToArray();
    if (!report.Success || actual.Length != 12 || warm.Length != warmups || report.ResultStatistics == null)
        throw new InvalidOperationException("Incomplete declared measurements.");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scope = "Fixed paired candidate comparison; same original measured benchmark binary per side, 32 warmups and 12 actual iterations.",
        @case = Name(report.BenchmarkCase), ns = Math.Round(report.ResultStatistics.Median, 2),
        warmupCount = warmups, actualIterations = actual.Length, iterationMilliseconds = 150,
        actualMeasurements = actual.Select(m => new { m.Operations, m.Nanoseconds }).ToArray(),
        warmupMeasurements = warm.Select(m => new { m.Operations, m.Nanoseconds }).ToArray(),
        operationsPerInvoke = report.BenchmarkCase.Descriptor.OperationsPerInvoke,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        serverGC = GCSettings.IsServerGC, gcLatencyMode = GCSettings.LatencyMode.ToString(),
        affinityMask = OperatingSystem.IsLinux() || OperatingSystem.IsWindows() ? (long?)Process.GetCurrentProcess().ProcessorAffinity : null,
        libraries = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && a.GetName().Name!.StartsWith("MethodCache", StringComparison.Ordinal))
            .Select(a => new { name = a.GetName().Name, location = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray()
    }));
}
if (matched != 1) throw new InvalidOperationException("Requested case not found.");
static string Name(BenchmarkCase c) => $"{c.Descriptor.Type.Name}.{c.Descriptor.WorkloadMethod.Name}({string.Join(",", c.Parameters.Items.Select(p => $"{p.Name}={p.Value}"))})";
