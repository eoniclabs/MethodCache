using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// One timing launch: the matching cases in this process, with a short fixed job. Cases timed in one process shape each
// other's JIT and GC state, so cost-profile.sh --time starts one launch per case and revision, alternating the sides,
// so drift on the machine hits both sides of a pair alike.
internal static class Timing
{
    internal static async Task<int> RunAsync(Type[] types, Func<string, bool> matches, bool hasRedis, bool hasSql, string output)
    {
        var job = Job.Default.WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(4).WithIterationCount(12).WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(150));
        var config = ManualConfig.CreateEmpty().AddJob(job).AddLogger(NullLogger.Instance).WithOptions(ConfigOptions.DisableLogFile);
        await using var writer = new StreamWriter(output, append: false);
        foreach (var type in types)
        {
            var all = BenchmarkConverter.TypeToBenchmarks(type, config);
            var cases = all.BenchmarksCases
                .Where(c => matches(Name(c)))
                .Where(c => c.Parameters.Items.All(p => (hasRedis || !Equals(p.Value, "Redis")) && (hasSql || !Equals(p.Value, "SqlServer"))))
                .ToArray();
            if (cases.Length == 0) continue;
            var summary = BenchmarkRunner.Run(new BenchmarkRunInfo(cases, type, all.Config));
            foreach (var report in summary.Reports)
            {
                var line = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["case"] = Name(report.BenchmarkCase),
                    ["ns"] = report.ResultStatistics is { } statistics ? Math.Round(statistics.Median / report.BenchmarkCase.Descriptor.OperationsPerInvoke, 2) : null,
                });
                await writer.WriteLineAsync(line);
                Console.WriteLine(line);
            }
        }
        return 0;
    }

    private static string Name(BenchmarkCase c)
        => $"{c.Descriptor.Type.Name}.{c.Descriptor.WorkloadMethod.Name}({string.Join(",", c.Parameters.Items.Select(p => $"{p.Name}={p.Value}"))})";
}
