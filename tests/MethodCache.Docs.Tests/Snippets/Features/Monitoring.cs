using System.Diagnostics.Metrics;
using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

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

    public static void StatsEndpoint(WebApplication app)
    {
        // begin-snippet: monitoring-stats-endpoint
        // Hits, misses, evictions and entries of this instance, as JSON; protect it like any admin endpoint
        app.MapGet("/health/cache", async (ICacheAdministrator admin, CancellationToken ct) =>
            Results.Ok(await admin.GetStatsAsync(ct)));
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

    public static IDisposable PrometheusNetRegistry(CollectorRegistry registry)
    {
        // begin-snippet: monitoring-prometheus-net-registry
        // A registry of your own needs both settings: with only Registry, no MethodCache series appear
        var adapter = MeterAdapter.StartListening(new MeterAdapterOptions
        {
            Registry = registry,
            MetricFactory = Metrics.WithCustomRegistry(registry),
            // Without a filter the adapter exports every meter in the process, ASP.NET Core's and the runtime's too
            InstrumentFilterPredicate = instrument => instrument.Meter.Name == MethodCacheTelemetry.Name,
        });
        // end-snippet
        return adapter;
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
