using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using MethodCache.WebDemo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Docs.Tests;

/// <summary>Actual web hosts and Redis. Owns its container, so the outage test never stops an application's Redis.</summary>
[Trait("Category", "RedisDemo")]
public class WebDemoRedisTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private RedisContainer? _redis;
    private readonly Func<RedisContainer> _createRedis;
    private readonly bool _infrastructureRequired;
    private readonly string _prefix = $"webdemo-test:{Guid.NewGuid():N}:";
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private string? _unavailable;

    public WebDemoRedisTests(ITestOutputHelper output) : this(output, CreateRedis,
        string.Equals(Environment.GetEnvironmentVariable("METHODCACHE_REQUIRE_INFRA_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
    { }

    internal WebDemoRedisTests(ITestOutputHelper output, Func<RedisContainer> createRedis, bool infrastructureRequired)
    {
        _output = output;
        _infrastructureRequired = infrastructureRequired;
        _createRedis = createRedis;
    }

    private static RedisContainer CreateRedis()
    {
        // Docker can assign a different ephemeral port after stop/start. Keep this test's endpoint stable.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new RedisBuilder("redis:7-alpine").WithPortBinding(port, 6379).Build();
    }

    public async Task InitializeAsync()
    {
        try
        {
            _redis = _createRedis();
            await _redis.StartAsync();
        }
        catch (Exception ex) when (!_infrastructureRequired)
        {
            _unavailable = $"Docker is required for the web demo Redis scenarios ({ex.GetType().Name}).";
        }
    }

    public async Task DisposeAsync()
    {
        if (_redis != null) await _redis.DisposeAsync();
    }

    internal async Task<ConnectionMultiplexer> ConnectAsync()
    {
        Skip.If(_unavailable != null, _unavailable);
        var options = ConfigurationOptions.Parse(_redis!.GetConnectionString());
        options.AbortOnConnectFail = false;
        options.BacklogPolicy = BacklogPolicy.FailFast;
        options.ConnectTimeout = 1000;
        options.ReconnectRetryPolicy = new ExponentialRetry(100);
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    private DemoWebFactory CreateFactory(IConnectionMultiplexer connection, string? dataFile = null) => new(dataFile: dataFile, clock: _clock, configure: services =>
    {
        services.AddMethodCacheRedis(options =>
        {
            options.ConnectionMultiplexerFactory = _ => connection;
        });
        services.Configure<DistributedCacheOptions>(options =>
        {
            options.KeyPrefix = _prefix;
            options.CircuitBreakerDuration = TimeSpan.FromSeconds(1);
        });
    });

    [SkippableFact]
    public async Task TwoInstances_RedisSharesReads_AndBackplaneInvalidatesOnlyChangedProducts()
    {
        using var connectionA = await ConnectAsync();
        using var connectionB = await ConnectAsync();
        await using var first = CreateFactory(connectionA);
        await using var second = CreateFactory(connectionB, first.DataFile);
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        await EventuallyAsync(async () => await BackplaneConnectedAsync(first) && await BackplaneConnectedAsync(second));
        var original = await a.GetFromJsonAsync<Product>("/products/1");
        Assert.Equal(original, await b.GetFromJsonAsync<Product>("/products/1"));
        var other = await a.GetFromJsonAsync<Product>("/products/2");
        Assert.Equal(other, await b.GetFromJsonAsync<Product>("/products/2"));
        Assert.Equal(2, first.Services.GetRequiredService<ProductCatalog>().Queries);
        Assert.Equal(0, second.Services.GetRequiredService<ProductCatalog>().Queries);
        _output.WriteLine("Two products: A executed 2 SQLite reads; B executed 0 (both Redis hits).");
        await b.GetFromJsonAsync<Product[]>("/products?category=keyboards");
        _clock.Advance(TimeSpan.FromSeconds(3));

        using var update = await a.PutAsJsonAsync("/products/1/price", new { price = 119m });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        await EventuallyAsync(async () => (await b.GetFromJsonAsync<Entry[]>("/methodcache/entries"))!
            .All(entry => !entry.Tags.Contains("product:1") && !entry.Tags.Contains("product-lists")));

        Assert.Equal(other, await b.GetFromJsonAsync<Product>("/products/2"));
        Assert.Equal(1, second.Services.GetRequiredService<ProductCatalog>().Queries);
        Assert.Equal(119m, (await b.GetFromJsonAsync<Product>("/products/1"))!.Price);
        Assert.Equal(119m, (await b.GetFromJsonAsync<Product[]>("/products?category=keyboards"))!.Single(p => p.Id == 1).Price);
        Assert.Equal(3, second.Services.GetRequiredService<ProductCatalog>().Queries);
        _output.WriteLine("Price update: B reloaded product 1 and its list; product 2 stayed cached.");

        using var invalidate = await a.PostAsJsonAsync("/methodcache/invalidate", new { tags = new[] { "products" } });
        Assert.Equal(HttpStatusCode.NoContent, invalidate.StatusCode);
        await EventuallyAsync(async () => (await b.GetFromJsonAsync<Entry[]>("/methodcache/entries"))!.Length == 0);
    }

    [SkippableFact]
    public async Task RedisOutage_MemoryAndDatabaseKeepWorking_QueuedInvalidationReachesOtherNodeAfterRestart()
    {
        using var connectionA = await ConnectAsync();
        using var connectionB = await ConnectAsync();
        await using var first = CreateFactory(connectionA);
        await using var second = CreateFactory(connectionB, first.DataFile);
        using var a = first.CreateClient();
        using var b = second.CreateClient();
        await EventuallyAsync(async () => await BackplaneConnectedAsync(first) && await BackplaneConnectedAsync(second));
        var original = await a.GetFromJsonAsync<Product>("/products/1");
        Assert.Equal(original, await b.GetFromJsonAsync<Product>("/products/1"));
        await a.GetFromJsonAsync<Product>("/products/2");
        _clock.Advance(TimeSpan.FromSeconds(3));

        var disconnectedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionA.ConnectionFailed += (_, args) => { if (args.ConnectionType == ConnectionType.Interactive) disconnectedA.TrySetResult(); };
        connectionB.ConnectionFailed += (_, args) => { if (args.ConnectionType == ConnectionType.Interactive) disconnectedB.TrySetResult(); };
        await _redis!.StopAsync();
        await Task.WhenAll(disconnectedA.Task, disconnectedB.Task).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(99m, (await a.GetFromJsonAsync<Product>("/products/2"))!.Price);
        Assert.Equal(2, first.Services.GetRequiredService<ProductCatalog>().Queries);
        Assert.Equal(449m, (await a.GetFromJsonAsync<Product>("/products/4"))!.Price);
        Assert.Equal("Degraded", await a.GetStringAsync("/health"));
        _output.WriteLine("Redis stopped: memory hits and cold SQLite reads still work; health is Degraded.");
        using var update = await a.PutAsJsonAsync("/products/1/price", new { price = 119m });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.Equal(119m, (await a.GetFromJsonAsync<Product>("/products/1"))!.Price);
        // Disconnected B can retain its L1 value until the retry/backplane arrives.
        Assert.Equal(original, await b.GetFromJsonAsync<Product>("/products/1"));
        _output.WriteLine("Price changed during the outage: A sees 119; disconnected B can still hold the old value.");

        var restoredA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restoredB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connectionA.ConnectionRestored += (_, args) => { if (args.ConnectionType == ConnectionType.Interactive) restoredA.TrySetResult(); };
        connectionB.ConnectionRestored += (_, args) => { if (args.ConnectionType == ConnectionType.Interactive) restoredB.TrySetResult(); };
        await _redis.StartAsync();
        await Task.WhenAll(restoredA.Task, restoredB.Task).WaitAsync(TimeSpan.FromSeconds(15));
        // Interactive socket recovery can precede subscription acknowledgement. Do not
        // fire the fake-clock retry while the backplane is still unable to publish.
        await EventuallyAsync(async () => await BackplaneConnectedAsync(first) && await BackplaneConnectedAsync(second));
        _clock.Advance(TimeSpan.FromSeconds(2));
        await EventuallyAsync(async () => await a.GetStringAsync("/health") == "Healthy");
        await EventuallyAsync(async () => (await b.GetFromJsonAsync<Entry[]>("/methodcache/entries"))!
            .All(entry => !entry.Tags.Contains("product:1")));
        Assert.Equal(119m, (await b.GetFromJsonAsync<Product>("/products/1"))!.Price);
        _output.WriteLine("Redis restarted: the fake clock advanced the retry window, health recovered, B sees 119.");
    }

    private static async Task<bool> BackplaneConnectedAsync(DemoWebFactory factory)
    {
        var report = await factory.Services.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>().CheckHealthAsync();
        return report.Entries["methodcache"].Data.TryGetValue("backplaneStatus", out var state) && Equals(state, "connected");
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition())
        {
            guard.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private sealed record Entry(string[] Tags);
}
