using System.Diagnostics;
using MethodCache;
using MethodCache.WebDemo;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Your service, registered as usual. AddMethodCache() wraps it in the generated cache decorator.
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ProductStore>();
builder.Services.AddSingleton<ProductCatalog>();
builder.Services.AddSingleton<IProductCatalog>(sp => sp.GetRequiredService<ProductCatalog>());
builder.Services.AddMethodCache(methodCache => methodCache.FromConfiguration(builder.Configuration)); // "MethodCache" in appsettings.json, reloaded live

// Optional: share the cache between instances. Set ConnectionStrings:Redis (the "redis" launch profiles do).
var redis = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redis))
{
    builder.Services.AddMethodCacheRedis(options =>
    {
        options.Configuration = redis;
    });
    builder.Services.Configure<DistributedCacheOptions>(options =>
        options.KeyPrefix = builder.Configuration["Demo:CachePrefix"] ?? "methodcache-webdemo:");
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
// The demo has no sign-in page. Outside Development, protected API routes
// challenge with 401/403 instead of redirecting to a login page.
builder.Services.AddAuthentication("Cookies").AddCookie("Cookies", options =>
{
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireRole("Admin")));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

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
      GET  /demo/queries                real database reads (Development only)
      POST /demo/reset                  restore seeded products and invalidate their cache (Development only)
    """));

app.MapGet("/products/{id:int}", async (int id, IProductCatalog catalog) =>
    await catalog.GetProductAsync(id) is { } product ? Results.Ok(product) : Results.NotFound());

app.MapGet("/products", (string category, IProductCatalog catalog) => catalog.GetByCategoryAsync(category));

app.MapPut("/products/{id:int}/price", async (int id, PriceChange change, IProductCatalog catalog) =>
{
    if (change.Price is not { } price || price < 0) return Results.BadRequest(new { error = "Price is required and must be non-negative." });
    try { await catalog.UpdatePriceAsync(id, price); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    return Results.NoContent();
});

// The admin endpoints expose cache keys and can empty the cache, so they require an authorization policy.
// This demo opens them in Development only.
var admin = app.MapMethodCacheAdmin("CacheAdmins");
if (app.Environment.IsDevelopment())
{
    admin.AllowAnonymous();
    app.MapGet("/demo/queries", (ProductCatalog catalog) => new { queries = catalog.Queries });
    app.MapPost("/demo/reset", async (IProductCatalog catalog) =>
    {
        await catalog.ResetAsync();
        return Results.NoContent();
    });
}

app.MapHealthChecks("/health");

app.Run();

internal sealed record PriceChange(decimal? Price);
