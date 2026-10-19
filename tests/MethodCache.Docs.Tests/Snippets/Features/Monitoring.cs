using System.Diagnostics.Metrics;
using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Monitoring;

public static class Setup
{
    public static async Task Administrator(ICacheAdministrator admin)
    {
        // begin-snippet: monitoring-administrator
        var stats = await admin.GetStatsAsync();
        var ratio = stats.Hits + stats.Misses == 0 ? 0 : (double)stats.Hits / (stats.Hits + stats.Misses);
        Console.WriteLine($"Hit ratio {ratio:P0}, {stats.LocalMethodEntries} local method entries");
        Console.WriteLine($"HTTP: {stats.LocalHttpEntries} local responses, {stats.LocalHttpBytes} stored bytes");
        Console.WriteLine($"Method weight: {stats.TotalWeight} (configured weight units)");

        foreach (var method in admin.GetMethods())
            Console.WriteLine($"{method.MethodId}: {method.Options.Duration}");

        var entries = admin.ListEntries(tag: "user:42");
        await admin.InvalidateTagsAsync(["user:42"]);
        // end-snippet
    }

    public static void ConsoleMetrics(WebApplicationBuilder builder)
    {
        // begin-snippet: monitoring-console-metrics
        builder.Services.AddMethodCache();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, active) =>
        {
            if (instrument.Meter.Name == MethodCacheTelemetry.Name)
                active.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            Console.WriteLine($"{instrument.Name}: {value}"));
        listener.Start(); // keep the listener alive for as long as measurements are needed
        // end-snippet
    }

    public static void CustomMetrics(WebApplicationBuilder builder)
    {
        // begin-snippet: monitoring-metrics-provider-registration
        // Core emits standard Meter and ActivitySource instruments
        builder.Services.AddMethodCache(mc => mc.Telemetry(options =>
        {
            options.RecordCacheKeys = false;
            options.RecordCacheTags = false;
        }));
        // end-snippet
    }
}
