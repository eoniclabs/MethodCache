using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

// Records the runtime's allocation ticks (one about every 100 KB, with the type and call stack that crossed it) over
// an EventPipe session on this process. Over thousands of ticks the bytes attributed to each call site approach
// its real share, so two revisions can be compared site by site.
internal static class AllocationSites
{
    internal static async Task<Dictionary<string, object>> RunAsync(Action op, Action? iterationSetup, string? setupName,
        int perInvoke, double seconds, int invocationCap)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cost-profile-{Environment.ProcessId}-{Guid.NewGuid():N}.nettrace");
        var client = new DiagnosticsClient(Environment.ProcessId);
        EventPipeProvider[] providers = [new("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, 0x1)]; // GC: AllocationTick
        long operations = 0, measured, setupBytes = 0;
        using (var session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 512))
        {
            var copy = Task.Run(async () =>
            {
                await using var file = File.Create(path);
                await session.EventStream.CopyToAsync(file);
            });
            await Task.Delay(500);
            var before = GC.GetTotalAllocatedBytes(true);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // A case capped per iteration runs several iterations; ticks inside its setup are dropped below
            for (var inIteration = 0; watch.Elapsed.TotalSeconds < seconds; operations++, inIteration++)
            {
                if (inIteration == invocationCap)
                {
                    if (iterationSetup == null) break;
                    var setupBefore = GC.GetTotalAllocatedBytes(true);
                    iterationSetup();
                    setupBytes += GC.GetTotalAllocatedBytes(true) - setupBefore;
                    inIteration = 0;
                }
                op();
            }
            measured = GC.GetTotalAllocatedBytes(true) - before - setupBytes;
            session.Stop();
            await copy;
        }

        var sites = new Dictionary<string, long>();
        var etlx = TraceLog.CreateFromEventPipeDataFile(path);
        try
        {
            using var log = new TraceLog(etlx);
            foreach (var e in log.Events)
            {
                if (e is not GCAllocationTickTraceData tick) continue;
                var frames = new List<string>();
                for (var stack = e.CallStack(); stack != null && frames.Count < 60; stack = stack.Caller)
                    if (stack.CodeAddress.FullMethodName is { Length: > 0 } name) frames.Add(name);
                if (setupName != null && frames.Any(f => f.Contains("." + setupName, StringComparison.Ordinal))) continue;
                // The allocated type, the three innermost frames, and the innermost MethodCache frame that led there
                var owner = frames.FirstOrDefault(f => f.StartsWith("MethodCache.", StringComparison.Ordinal)) ?? "?";
                var key = $"{tick.TypeName} @ {string.Join(" < ", frames.Take(3).Select(Short))} || {Short(owner)}";
                sites[key] = sites.GetValueOrDefault(key) + tick.AllocationAmount64;
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(etlx);
        }

        var perOperation = (double)operations * perInvoke;
        return new Dictionary<string, object>
        {
            ["bytes"] = Math.Round(measured / perOperation, 1),
            ["operations"] = operations,
            ["sites"] = sites.OrderByDescending(p => p.Value).Take(80).ToDictionary(p => p.Key, p => Math.Round(p.Value / perOperation, 1)),
        };
    }

    private static string Short(string name)
    {
        var parameters = name.IndexOf('(', StringComparison.Ordinal);
        if (parameters > 0) name = name[..parameters];
        return name.Length <= 140 ? name : name[..140];
    }
}
