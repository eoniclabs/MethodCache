using System.Diagnostics;
using MethodCache.WebDemo;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Your service, registered as usual. AddMethodCache() wraps it in the generated cache decorator.
builder.Services.AddSingleton<IProductCatalog, ProductCatalog>();
builder.Services.AddMethodCache(methodCache => methodCache.FromConfiguration(builder.Configuration)); // "MethodCache" in appsettings.json, reloaded live

// Optional: share the cache between instances. Set ConnectionStrings:Redis (the "redis" launch profiles do).
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redis))
{
    builder.Services.AddMethodCacheRedis(redis);
}

// Traces for cache misses and invalidations, metrics for every call. Exported when OTEL_EXPORTER_OTLP_ENDPOINT is set.
var telemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "MethodCache.WebDemo"))
    .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddMethodCacheInstrumentation())
    .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMethodCacheInstrumentation());
if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

builder.Services.AddHealthChecks().AddMethodCache();
builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireRole("Admin")));

var app = builder.Build();

// Shows how long each request took, so a cache hit is easy to spot
app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Elapsed-Ms"] = stopwatch.Elapsed.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    });
    await next(context);
});

app.MapGet("/", () => Results.Text("""
    MethodCache web demo. Try:
      GET  /products/1                  300 ms the first time, then from the cache
      GET  /products?category=keyboards a cached list
      PUT  /products/1/price  {"price": 119}   invalidates product 1 and the lists, nothing else
      GET  /methodcache/stats           hits, misses and entries
      GET  /methodcache/entries         what is cached right now
      GET  /methodcache/methods         every cached method's policy and where it came from
      GET  /health                      health check
    """));

app.MapGet("/products/{id:int}", async (int id, IProductCatalog catalog) =>
    await catalog.GetProductAsync(id) is { } product ? Results.Ok(product) : Results.NotFound());

app.MapGet("/products", (string category, IProductCatalog catalog) => catalog.GetByCategoryAsync(category));

app.MapPut("/products/{id:int}/price", async (int id, PriceChange change, IProductCatalog catalog) =>
{
    await catalog.UpdatePriceAsync(id, change.Price);
    return Results.NoContent();
});

// The admin endpoints expose cache keys and can empty the cache, so they require an authorization policy.
// This demo opens them in Development only.
var admin = app.MapMethodCacheAdmin("CacheAdmins");
if (app.Environment.IsDevelopment())
{
    admin.AllowAnonymous();
}

app.MapHealthChecks("/health");

app.Run();

internal sealed record PriceChange(decimal Price);
