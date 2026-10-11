using System.Diagnostics;
using System.Text;
using FxVendor;
using MethodCache.Core;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Management;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.Demo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// Chapter 5: caches the vendor's own interface, no wrapper. Only the listed method is cached.
[assembly: CacheInterface(typeof(IFxRatesClient), nameof(IFxRatesClient.GetRateAsync), Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]

Console.OutputEncoding = Encoding.UTF8;

// appsettings.json is copied to a temp file so chapter 4 can edit it while the app runs.
var settingsFile = Path.Combine(Path.GetTempPath(), $"methodcache-demo-{Guid.NewGuid():N}.json");
File.Copy(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), settingsFile);

try
{
    var configuration = new ConfigurationBuilder()
        .AddJsonFile(settingsFile, optional: false, reloadOnChange: true)
        .Build();

    // ── The whole setup ──────────────────────────────────────────────────────
    var services = new ServiceCollection();
    services.AddSingleton(typeof(CallCounter<>));
    services.AddSingleton<FxRatesClient>();
    services.AddSingleton<IFxRatesClient>(sp => sp.GetRequiredService<FxRatesClient>());

    services.AddSingleton<IWeatherService, WeatherService>();
    services.AddSingleton<IUserProfiles, UserProfiles>();
    services.AddSingleton<IProductCatalog, ProductCatalog>();

    services.AddMethodCache(config => config.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(10))));
    services.AddMethodCacheFromConfiguration(configuration);
    // ─────────────────────────────────────────────────────────────────────────

    await using var provider = services.BuildServiceProvider();

    Ui.Banner();
    await OneAttributeAsync(provider);
    await StampedeAsync(provider);
    await TagsAsync(provider);
    await ConfigurationAsync(provider, settingsFile);
    await ManualCachingAsync(provider);
    await InsightAsync(provider);
    Ui.Summary(chapters: 6);
}
finally
{
    File.Delete(settingsFile);
}

return Ui.Failures == 0 ? 0 : 1;

static async Task OneAttributeAsync(IServiceProvider provider)
{
    Ui.Chapter(1, "One attribute", "Put [Cache] on an interface method and call AddMethodCache(). The source generator writes the decorator.");
    Ui.Code("""
        public interface IWeatherService
        {
            [Cache(Duration = "00:05:00", Tags = ["weather"])]
            Task<Forecast> GetForecastAsync(string city);
        }

        services.AddSingleton<IWeatherService, WeatherService>();
        services.AddMethodCache();
        """);

    var weather = provider.GetRequiredService<IWeatherService>();
    var counter = provider.GetRequiredService<CallCounter<IWeatherService>>();

    var stopwatch = Stopwatch.StartNew();
    var first = await weather.GetForecastAsync("Stockholm");
    var missTime = stopwatch.Elapsed;

    stopwatch.Restart();
    var second = await weather.GetForecastAsync("Stockholm");
    var hitTime = stopwatch.Elapsed;

    const int calls = 1_000_000;
    for (var i = 0; i < 100_000; i++)
    {
        await weather.GetForecastAsync("Stockholm"); // Warm up the JIT before measuring
    }

    stopwatch.Restart();
    for (var i = 0; i < calls; i++)
    {
        await weather.GetForecastAsync("Stockholm");
    }

    var perHit = stopwatch.Elapsed.TotalNanoseconds / calls;

    Ui.Step("First call (runs the method)", Ui.Ms(missTime));
    Ui.Step("Second call (from the cache)", Ui.Ms(hitTime));
    Ui.Step($"Average of {calls:N0} cached calls", $"{perHit,7:0} ns{BuildNote}");
    Ui.Step("Resolved type", weather.GetType().Name);
    Ui.Check(ReferenceEquals(first, second), "The second call returned the same result without running the method");
    Ui.Check(counter.Calls == 1, $"WeatherService ran {Ui.Times(counter.Calls)} for {calls + 100_002:N0} calls");
}

static async Task StampedeAsync(IServiceProvider provider)
{
    Ui.Chapter(2, "Stampede protection", "100 requests ask for the same uncached value at the same moment. Only one of them runs the method.");

    var weather = provider.GetRequiredService<IWeatherService>();
    var counter = provider.GetRequiredService<CallCounter<IWeatherService>>();
    counter.Reset();

    var stopwatch = Stopwatch.StartNew();
    var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => weather.GetForecastAsync("Malmö"))));

    Ui.Step("100 concurrent calls finished in", Ui.Ms(stopwatch.Elapsed));
    Ui.Check(counter.Calls == 1, $"WeatherService ran {Ui.Times(counter.Calls)}");
    Ui.Check(results.All(r => ReferenceEquals(r, results[0])), "All 100 callers got the same result");
}

static async Task TagsAsync(IServiceProvider provider)
{
    Ui.Chapter(3, "Tags that understand your arguments", "\"user:{userId}\" becomes user:1, user:2 ... Renaming user 1 invalidates user 1 and nothing else.");
    Ui.Code("""
        [Cache(Duration = "00:10:00", Tags = ["users", "user:{userId}"])]
        Task<UserProfile> GetProfileAsync(int userId);

        [CacheInvalidate(Tags = ["user:{userId}"])]
        Task RenameAsync(int userId, string newName);
        """);

    var users = provider.GetRequiredService<IUserProfiles>();
    var counter = provider.GetRequiredService<CallCounter<IUserProfiles>>();

    await users.GetProfileAsync(1);
    await users.GetProfileAsync(2);
    await users.GetProfileAsync(1);
    await users.GetProfileAsync(2);
    Ui.Step("Load users 1 and 2, twice each", $"{counter.Calls} database queries");

    await users.RenameAsync(1, "Ada King");
    var ada = await users.GetProfileAsync(1);
    var alan = await users.GetProfileAsync(2);
    Ui.Step("Rename user 1, load both again", $"{counter.Calls - 2} new database query");

    Ui.Check(ada.Name == "Ada King", $"User 1 is fresh: {ada.Name}");
    Ui.Check(counter.Calls == 3 && alan.Name == "Alan Turing", $"User 2 still came from the cache: {alan.Name}");
}

static async Task ConfigurationAsync(IServiceProvider provider, string settingsFile)
{
    Ui.Chapter(4, "Change policies without touching code", "Durations can come from the attribute, a global default, appsettings.json (reloaded live) or a runtime override.");
    Ui.Code("""
        services.AddMethodCache(config => config.DefaultPolicy(o => o.WithDuration(TimeSpan.FromMinutes(10))));
        services.AddMethodCacheFromConfiguration(configuration);   // the "MethodCache" section
        """);

    var admin = provider.GetRequiredService<ICacheAdministrator>();
    var runtime = provider.GetRequiredService<IRuntimeCacheConfigurator>();
    const string productMethod = "MethodCache.Demo.IProductCatalog.GetProductAsync";

    PrintPolicies(admin, "At startup:");

    // Someone edits appsettings.json on the server. No restart.
    var json = await File.ReadAllTextAsync(settingsFile);
    await File.WriteAllTextAsync(settingsFile, json.Replace("00:15:00", "00:20:00", StringComparison.Ordinal));
    var reloaded = await WaitForAsync(() => DurationOf(admin, productMethod) == TimeSpan.FromMinutes(20));
    PrintPolicies(admin, "After editing appsettings.json (15 → 20 min):");
    Ui.Check(reloaded, "appsettings.json was reloaded while the app was running");

    // An operator shortens the duration during an incident.
    await runtime.ApplyAsync(config => config
        .ForService<IProductCatalog>()
        .Method(c => c.GetProductAsync(default!))
        .Configure(o => o.WithDuration(TimeSpan.FromMinutes(1))));

    var catalog = provider.GetRequiredService<IProductCatalog>();
    await catalog.GetProductAsync("KB-1");
    var key = MethodCacheKeys.For(typeof(IProductCatalog), nameof(IProductCatalog.GetProductAsync), MethodCacheKeys.Part("KB-1"));
    var entry = admin.GetEntry(key);
    Ui.Step("Runtime override, then a call", $"entry {key} lives {Ui.Duration(entry?.TimeToLive)}");
    Ui.Check(entry?.TimeToLive is { TotalSeconds: > 50 and <= 60 }, "The runtime override applied to new entries at once");

    await runtime.ClearAsync();
    Ui.Check(DurationOf(admin, productMethod) == TimeSpan.FromMinutes(20), "Clearing the override restored the appsettings.json value");
}

static async Task ManualCachingAsync(IServiceProvider provider)
{
    Ui.Chapter(5, "Cache code you don't own", "IFxRatesClient comes from a vendor's package. One line caches its rate lookups; orders still reach the API.");
    Ui.Code("""
        [assembly: CacheInterface(typeof(IFxRatesClient), nameof(IFxRatesClient.GetRateAsync),
            Duration = "00:01:00", Tags = ["fx", "fx:{from}"])]

        services.AddSingleton<IFxRatesClient, FxRatesClient>();   // the vendor's client, as usual
        """);

    var fx = provider.GetRequiredService<IFxRatesClient>();
    var client = provider.GetRequiredService<FxRatesClient>();

    var stopwatch = Stopwatch.StartNew();
    for (var i = 0; i < 3; i++)
    {
        await fx.GetRateAsync("EUR", "SEK");
    }

    Ui.Step("Three lookups of EUR → SEK", Ui.Ms(stopwatch.Elapsed));
    Ui.Check(client.RateCalls == 1, $"The exchange-rate API was called {Ui.Times(client.RateCalls)}");

    await fx.PlaceOrderAsync("EUR", "SEK", 100m);
    await fx.PlaceOrderAsync("EUR", "SEK", 100m);
    Ui.Check(client.OrderCalls == 2, "PlaceOrderAsync isn't listed, so both orders reached the API");
}

static async Task InsightAsync(IServiceProvider provider)
{
    Ui.Chapter(6, "See what is cached", "ICacheAdministrator (and the MethodCache.AspNetCore endpoints) show stats and live entries, and can invalidate them.");

    var admin = provider.GetRequiredService<ICacheAdministrator>();
    var stats = await admin.GetStatsAsync();

    Ui.Step("Hits / misses", $"{stats.Hits:N0} / {stats.Misses:N0}  (hit ratio {stats.HitRatio:P1})");
    Ui.Step("Entries", $"{stats.Entries:N0}");
    Ui.WriteEntries(admin.ListEntries(limit: 10));

    var before = admin.ListEntries(tag: "weather").Count;
    await admin.InvalidateTagsAsync(["weather"]);
    var after = admin.ListEntries(tag: "weather").Count;
    Ui.Step("Invalidate tag \"weather\"", $"{before} → {after} entries");

    Ui.Check(stats.Hits > 1_000_000 && stats.Misses > 0, "Every hit and miss is counted, including the million in chapter 1");
    Ui.Check(before > 0 && after == 0, "Tag invalidation removed every weather entry");
}

static void PrintPolicies(ICacheAdministrator admin, string title)
{
    Ui.Step(title, "");
    Ui.Table(
        ["Method", "Duration", "Comes from"],
        admin.GetMethods()
            .Where(m => m.MethodId.Contains(".IProductCatalog.", StringComparison.Ordinal) || m.MethodId.Contains(".IWeatherService.", StringComparison.Ordinal))
            .OrderBy(m => m.MethodId, StringComparer.Ordinal)
            .Select(m => (IReadOnlyList<string>)[ShortName(m.MethodId), Ui.Duration(m.Duration), string.Join(", ", m.Sources)]));
}

static TimeSpan? DurationOf(ICacheAdministrator admin, string methodId) =>
    admin.GetMethods().FirstOrDefault(m => m.MethodId == methodId)?.Duration;

static string ShortName(string methodId) => methodId["MethodCache.Demo.".Length..];

static async Task<bool> WaitForAsync(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!condition())
    {
        if (DateTime.UtcNow > deadline)
        {
            return false;
        }

        await Task.Delay(100);
    }

    return true;
}

#if DEBUG
internal static partial class Program
{
    private const string BuildNote = "  (Debug build: add -c Release for real numbers)";
}
#else
internal static partial class Program
{
    private const string BuildNote = "";
}
#endif
