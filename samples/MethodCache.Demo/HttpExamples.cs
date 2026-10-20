using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace MethodCache.Demo;

/// <summary>Runs the exact same local HTTP scenarios in the demo and the docs tests.</summary>
public static class HttpExamples
{
    public static async Task<IReadOnlyList<ExampleCheck>> RunAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var origin = builder.Build();
        var productCalls = 0;
        var validations = 0;
        var liveCalls = 0;
        var writes = 0;
        var price = 50m;
        var revision = 1;
        var stateLock = new object();

        origin.MapGet("/product", (HttpContext context) =>
        {
            Interlocked.Increment(ref productCalls);
            lock (stateLock)
            {
                var etag = $"\"product-{revision}\"";
                context.Response.Headers.CacheControl = "public, max-age=60";
                context.Response.Headers.ETag = etag;
                context.Response.Headers.Date = clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture);
                if (context.Request.Headers.IfNoneMatch == etag)
                {
                    Interlocked.Increment(ref validations);
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }
                return Results.Json(new { price, revision });
            }
        });
        origin.MapPut("/product", (PriceChange change) =>
        {
            lock (stateLock) { price = change.Price; revision++; }
            Interlocked.Increment(ref writes);
            return Results.NoContent();
        });
        origin.MapGet("/live", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text($"Live response {Interlocked.Increment(ref liveCalls)}");
        });
        await origin.StartAsync();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddMethodCache();
        // Cache-visible identity must not be changed by an automatic transport cookie jar.
        services.AddHttpClient<ProductApiClient>(http => http.BaseAddress = new Uri(origin.Urls.Single()))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false })
            .AddHttpCaching(options => options.AddDiagnosticHeaders = true);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ProductApiClient>();
        var checks = new List<ExampleCheck>();

        var first = await client.GetAsync("/product");
        var second = await client.GetAsync("/product");
        checks.Add(new($"max-age: two GETs, {productCalls} origin call, Cache-Status: {second.CacheOutcome}",
            productCalls == 1 && second.Body == first.Body && second.CacheOutcome?.StartsWith("MethodCache; hit;", StringComparison.Ordinal) == true));

        clock.Advance(TimeSpan.FromSeconds(61));
        var validated = await client.GetAsync("/product");
        checks.Add(new($"ETag: origin returned {validations} HTTP 304, caller receives 200 with the original body",
            productCalls == 2 && validations == 1 && validated.Status == HttpStatusCode.OK && validated.Body == first.Body && validated.CacheOutcome?.StartsWith("MethodCache; fwd=stale; fwd-status=304", StringComparison.Ordinal) == true));

        var live1 = await client.GetAsync("/live");
        var live2 = await client.GetAsync("/live");
        checks.Add(new($"no-store: two GETs make {liveCalls} origin calls and return different bodies",
            liveCalls == 2 && live1.Body != live2.Body));

        await client.UpdatePriceAsync(75m);
        var updated = await client.GetAsync("/product");
        using var json = JsonDocument.Parse(updated.Body);
        checks.Add(new($"PUT: cached GET is invalidated; price is 75, origin GET count is {productCalls}",
            writes == 1 && productCalls == 3 && json.RootElement.GetProperty("price").GetDecimal() == 75m && updated.CacheOutcome?.StartsWith("MethodCache; fwd=uri-miss;", StringComparison.Ordinal) == true));
        var cachedUpdate = await client.GetAsync("/product");
        checks.Add(new("The updated response is cached on the next GET", productCalls == 3 && cachedUpdate.Body == updated.Body && cachedUpdate.CacheOutcome?.StartsWith("MethodCache; hit;", StringComparison.Ordinal) == true));
        await origin.StopAsync();
        return checks;
    }

    private sealed record PriceChange(decimal Price);
}
