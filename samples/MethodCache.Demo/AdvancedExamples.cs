using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.Demo;

public sealed record ExampleCheck(string Claim, bool Passed);

/// <summary>The console and docs tests run these same scenarios. The clock advances explicitly, never by sleeping.</summary>
public static class AdvancedExamples
{
    private static ServiceProvider CreateProvider(FakeTimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<TimedReports>();
        services.AddSingleton<ITimedReports>(sp => sp.GetRequiredService<TimedReports>());
        services.AddSingleton<ResilientRates>();
        services.AddSingleton<IResilientRates>(sp => sp.GetRequiredService<ResilientRates>());
        services.AddSingleton<AvailableProducts>();
        services.AddSingleton<IAvailableProducts>(sp => sp.GetRequiredService<AvailableProducts>());
        services.AddSingleton<TenantCatalog>();
        services.AddSingleton<ITenantCatalog>(sp => sp.GetRequiredService<TenantCatalog>());
        services.AddMethodCache(config =>
        {
            config.Method((IAvailableProducts s) => s.FindAsync(Any<int>.Value))
                .CacheWhen(product => product is not null);
            config.Method((IResilientRates s) => s.GetAsync(Any<string>.Value))
                .Configure(options => options.WithFailSafe(TimeSpan.FromMinutes(1)).WithFailSafeRetryAfter(TimeSpan.FromSeconds(5)));
        });
        return services.BuildServiceProvider();
    }

    public static async Task<IReadOnlyList<ExampleCheck>> ExpirationAsync()
    {
        var clock = new FakeTimeProvider();
        await using var provider = CreateProvider(clock);
        var reports = provider.GetRequiredService<ITimedReports>();
        var source = provider.GetRequiredService<TimedReports>();
        var checks = new List<ExampleCheck>();

        await reports.GetReportAsync();
        clock.Advance(TimeSpan.FromSeconds(9));
        checks.Add(new("9 seconds: the report is still cached (1 source call)", await reports.GetReportAsync() == 1 && source.ReportCalls == 1));
        clock.Advance(TimeSpan.FromSeconds(1));
        checks.Add(new("10 seconds: the report expires and reloads (2 source calls)", await reports.GetReportAsync() == 2 && source.ReportCalls == 2));

        var session = await reports.GetSessionAsync("active");
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(9));
            checks.Add(new($"Sliding window: read {i + 1} extends the session without loading", await reports.GetSessionAsync("active") == session));
        }
        clock.Advance(TimeSpan.FromSeconds(3));
        checks.Add(new("Absolute duration wins: the active session expires at 30 seconds", await reports.GetSessionAsync("active") != session));
        session = await reports.GetSessionAsync("idle");
        clock.Advance(TimeSpan.FromSeconds(11));
        checks.Add(new("An idle session expires after its 10-second sliding window", await reports.GetSessionAsync("idle") != session));

        await reports.GetPricesAsync();
        clock.Advance(TimeSpan.FromSeconds(8));
        checks.Add(new("Refresh-ahead returns revision 1 while revision 2 is still loading", await reports.GetPricesAsync() == 1));
        await source.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.ReleaseRefresh.TrySetResult();
        await UntilAsync(async () => await reports.GetPricesAsync() == 2);
        checks.Add(new("Background refresh publishes revision 2 (exactly 2 source calls)", source.PriceCalls == 2));
        return checks;
    }

    public static async Task<IReadOnlyList<ExampleCheck>> FailSafeAsync()
    {
        var clock = new FakeTimeProvider();
        await using var provider = CreateProvider(clock);
        var rates = provider.GetRequiredService<IResilientRates>();
        var source = provider.GetRequiredService<ResilientRates>();
        var checks = new List<ExampleCheck>();
        await rates.GetAsync("EUR/SEK");
        clock.Advance(TimeSpan.FromSeconds(11));
        source.Failing = true;
        checks.Add(new("An API failure returns the expired last good value", await rates.GetAsync("EUR/SEK") == 100m));
        await rates.GetAsync("EUR/SEK");
        checks.Add(new("FailSafeRetryAfter prevents another API call during the outage", source.Calls == 2));
        source.Failing = false;
        source.Rate = 120m;
        clock.Advance(TimeSpan.FromSeconds(6));
        checks.Add(new("After the retry window, the recovered API replaces the fallback", await rates.GetAsync("EUR/SEK") == 120m && source.Calls == 3));

        clock.Advance(TimeSpan.FromSeconds(11));
        source.SlowResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = rates.GetAsync("EUR/SEK");
        await source.SlowCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(2));
        checks.Add(new("A slow API returns the fallback after the 2-second timeout", await slow.WaitAsync(TimeSpan.FromSeconds(10)) == 120m));
        source.SlowResponse.SetResult(130m);
        await UntilAsync(async () => await rates.GetAsync("EUR/SEK") == 130m);
        checks.Add(new("The slow response completes in the background and replaces the fallback", source.Calls == 4));

        source.SlowResponse = null;
        source.Failing = true;
        var threw = false;
        try { await rates.GetAsync("uncached-pair"); }
        catch (HttpRequestException) { threw = true; }
        checks.Add(new("Without a last good value, the original API error reaches the caller", threw));
        return checks;
    }

    public static async Task<IReadOnlyList<ExampleCheck>> ConditionalAsync()
    {
        await using var provider = CreateProvider(new FakeTimeProvider());
        var products = provider.GetRequiredService<IAvailableProducts>();
        var source = provider.GetRequiredService<AvailableProducts>();
        var first = await products.FindAsync(42);
        var second = await products.FindAsync(42);
        var checks = new List<ExampleCheck> { new("Missing products return null and are not stored (2 source calls)", first is null && second is null && source.Calls == 2) };
        source.Products[42] = "New keyboard";
        checks.Add(new("A newly created product is visible on the very next lookup", await products.FindAsync(42) == "New keyboard"));
        await products.FindAsync(42);
        checks.Add(new("Existing products are cached (3 source calls in total)", source.Calls == 3));
        return checks;
    }

    public static async Task<IReadOnlyList<ExampleCheck>> TenantsAsync()
    {
        await using var provider = CreateProvider(new FakeTimeProvider());
        var catalog = provider.GetRequiredService<ITenantCatalog>();
        var source = provider.GetRequiredService<TenantCatalog>();
        var alpha = await catalog.GetAsync(DemoTenants.Alpha, 1);
        var beta = await catalog.GetAsync(DemoTenants.Beta, 1);
        var checks = new List<ExampleCheck> { new("Product 1 has separate values for alpha (100) and beta (200)", alpha.Price == 100m && beta.Price == 200m) };
        checks.Add(new("Both tenants get their own cached object", ReferenceEquals(alpha, await catalog.GetAsync(DemoTenants.Alpha, 1)) && ReferenceEquals(beta, await catalog.GetAsync(DemoTenants.Beta, 1)) && source.Calls == 2));
        await catalog.UpdatePriceAsync(DemoTenants.Alpha, 1, 150m);
        checks.Add(new("Updating alpha refreshes alpha while beta remains cached", (await catalog.GetAsync(DemoTenants.Alpha, 1)).Price == 150m && ReferenceEquals(beta, await catalog.GetAsync(DemoTenants.Beta, 1)) && source.Calls == 3));
        return checks;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition())
        {
            guard.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
