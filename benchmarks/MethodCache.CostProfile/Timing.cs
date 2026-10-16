using System.Text.Json;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// One timing launch: every matching case once, in this process, with a short fixed job. cost-profile.sh --time starts
// one launch per revision in turn, so drift on the machine hits both sides of a pair alike.
internal static class Timing
{
    internal static async Task<int> RunAsync(Type[] types, string filter, bool hasRedis, bool hasSql, string output)
    {
        var job = Job.Default.WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(4).WithIterationCount(12).WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(150));
        var config = ManualConfig.CreateEmpty().AddJob(job).AddLogger(NullLogger.Instance).WithOptions(ConfigOptions.DisableLogFile);
        await using var writer = new StreamWriter(output, append: false);
        foreach (var type in types)
        {
            var all = BenchmarkConverter.TypeToBenchmarks(type, config);
            var cases = all.BenchmarksCases
                .Where(c => Name(c).Contains(filter, StringComparison.Ordinal))
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
