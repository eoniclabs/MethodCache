using System.Diagnostics;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using FluentAssertions;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Redis;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

/// <summary>
/// A real Redis behind Toxiproxy, so the network between MethodCache and Redis can turn slow, silent or broken. A
/// cache miss must still take no longer than the operation timeout (plus the method), and hits from memory must not
/// notice at all. Needs Docker; skipped without it unless <c>METHODCACHE_REQUIRE_INFRA_TESTS</c> is <c>true</c>.
/// </summary>
[Collection(ToxiproxyCollection.Name)]
public sealed class RedisToxiproxyTests : IAsyncLifetime
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(1);

    // Wall-clock slack on top of the timeout for a loaded CI machine; a miss that waited on Redis's own timeouts
    // (5 seconds and more) still fails
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(1500);

    private readonly ToxiproxyFixture _proxy;
    private readonly string _prefix = $"toxi:{Guid.NewGuid():N}:";
    private readonly List<ServiceProvider> _nodes = new();

    public RedisToxiproxyTests(ToxiproxyFixture proxy)
    {
        _proxy = proxy;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_proxy.UnavailableReason == null)
        {
            await _proxy.RemoveToxicsAsync();
        }

        foreach (var node in _nodes)
        {
            await node.DisposeAsync();
        }
    }

    private async Task<ICacheManager> CreateNode(bool enableBackplane = true)
    {
        var configuration = _proxy.RequireConfiguration();
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.Configure<DistributedCacheOptions>(options => options.KeyPrefix = _prefix);
        services.AddMethodCache(mc => mc.UseRedis(options =>
        {
            options.Configuration = configuration;
            options.EnableBackplane = enableBackplane;
        }));
        services.Configure<DistributedCacheOptions>(options =>
        {
            options.OperationTimeout = OperationTimeout;
            options.CircuitBreakerDuration = BreakDuration;
        });

        var provider = services.BuildServiceProvider();
        _nodes.Add(provider);

        var connection = await provider.GetRequiredService<RedisConnection>().GetAsync();
        await WaitUntil(() => Task.FromResult(connection.IsConnected), TimeSpan.FromSeconds(10));
        return provider.GetRequiredService<ICacheManager>();
    }

    private static ValueTask<string> Get(ICacheManager node, string key, string value) =>
        node.GetOrCreateAsync(key, _ => ValueTask.FromResult(value), options => options.WithDuration(TimeSpan.FromMinutes(5)));

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(50);
        }
    }

    [SkippableTheory]
    [InlineData("latency", "latency", 10_000)] // every answer arrives 10 seconds late
    [InlineData("timeout", "timeout", 0)] // nothing gets through and the connection stays open: Redis stopped answering
    [InlineData("reset_peer", "timeout", 0)] // the connection is reset by the peer
    public async Task Miss_WhenRedisMisbehaves_TakesAtMostTheOperationTimeout_AndHitsAreUnaffected(string toxic, string attribute, int value)
    {
        // Measure the cache data path in isolation. A pub/sub reconnect intentionally clears L1 and can race
        // warm-up, so it cannot promise a retained memory hit. The recovery test below keeps the backplane enabled.
        var node = await CreateNode(enableBackplane: false);
        (await Get(node, "hot", "hot")).Should().Be("hot");

        await _proxy.AddToxicAsync(toxic, new Dictionary<string, int> { [attribute] = value });

        var hit = Get(node, "hot", "unused");
        hit.IsCompletedSuccessfully.Should().BeTrue("a hit from memory doesn't touch Redis");
        (await hit).Should().Be("hot");

        var watch = Stopwatch.StartNew();
        (await Get(node, "cold", "loaded")).Should().Be("loaded");
        watch.Elapsed.Should().BeLessThan(OperationTimeout + Slack, "a {0} toxic must not hold a miss past the timeout", toxic);

        // The breaker is open now, so the next miss doesn't wait for Redis at all
        watch.Restart();
        (await Get(node, "colder", "loaded")).Should().Be("loaded");
        watch.Elapsed.Should().BeLessThan(Slack);
    }

    [SkippableFact]
    public async Task AfterTheNetworkHeals_InstancesShareValuesAgain()
    {
        var first = await CreateNode();
        var second = await CreateNode();
        await _proxy.AddToxicAsync("timeout", new Dictionary<string, int> { ["timeout"] = 0 });
        (await Get(first, "during", "first")).Should().Be("first");

        await _proxy.RemoveToxicsAsync();

        // Once the breaker lets a call through and the connection is back, a value written by one is read by the other
        await WaitUntil(async () =>
        {
            var key = $"after:{Guid.NewGuid():N}";
            await Get(first, key, "from first");
            return await Get(second, key, "from second") == "from first";
        }, TimeSpan.FromSeconds(30));
    }
}

/// <summary>
/// Starts Redis and Toxiproxy on one Docker network and a proxy from Toxiproxy to Redis; MethodCache connects to the
/// proxy. Toxics are added and removed through Toxiproxy's HTTP API.
/// </summary>
public sealed class ToxiproxyFixture : IAsyncLifetime
{
    private const string ProxyName = "redis";
    private const ushort ApiPort = 8474;
    private const ushort ProxyPort = 8666;

    private INetwork? _network;
    private RedisContainer? _redis;
    private IContainer? _toxiproxy;
    private HttpClient? _api;

    public string? Configuration { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _network = new NetworkBuilder().WithName($"methodcache-toxi-{Guid.NewGuid():N}").Build();
            await _network.CreateAsync();

            _redis = new RedisBuilder("redis:7-alpine")
                .WithNetwork(_network)
                .WithNetworkAliases("redis")
                .Build();

            _toxiproxy = new ContainerBuilder("ghcr.io/shopify/toxiproxy:2.12.0")
                .WithNetwork(_network)
                .WithPortBinding(ApiPort, true)
                .WithPortBinding(ProxyPort, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(ApiPort).ForPath("/version")))
                .Build();

            await Task.WhenAll(_redis.StartAsync(), _toxiproxy.StartAsync());

            _api = new HttpClient { BaseAddress = new Uri($"http://{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(ApiPort)}") };
            var created = await _api.PostAsJsonAsync("/proxies", new
            {
                name = ProxyName,
                listen = $"0.0.0.0:{ProxyPort}",
                upstream = "redis:6379",
                enabled = true,
            });
            created.EnsureSuccessStatusCode();

            Configuration = $"{_toxiproxy.Hostname}:{_toxiproxy.GetMappedPublicPort(ProxyPort)}";
        }
        catch (Exception ex) when (!InfrastructureRequired)
        {
            UnavailableReason = $"No Docker for Redis behind Toxiproxy ({ex.GetType().Name}: {ex.Message}).";
        }
    }

    public async Task DisposeAsync()
    {
        _api?.Dispose();
        if (_toxiproxy != null)
        {
            await _toxiproxy.DisposeAsync();
        }

        if (_redis != null)
        {
            await _redis.DisposeAsync();
        }

        if (_network != null)
        {
            await _network.DisposeAsync();
        }
    }

    public string RequireConfiguration()
    {
        Skip.If(UnavailableReason != null, UnavailableReason);
        return Configuration!;
    }

    /// <summary>Adds a toxic to the answers from Redis (or, for reset_peer and timeout, to the connection).</summary>
    public async Task AddToxicAsync(string type, IReadOnlyDictionary<string, int> attributes)
    {
        var response = await _api!.PostAsJsonAsync($"/proxies/{ProxyName}/toxics", new
        {
            name = type,
            type,
            stream = "downstream",
            toxicity = 1.0,
            attributes,
        });
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveToxicsAsync()
    {
        var toxics = await _api!.GetFromJsonAsync<List<Toxic>>($"/proxies/{ProxyName}/toxics") ?? new List<Toxic>();
        foreach (var toxic in toxics)
        {
            (await _api!.DeleteAsync($"/proxies/{ProxyName}/toxics/{toxic.Name}")).EnsureSuccessStatusCode();
        }
    }

    private sealed record Toxic(string Name);

    private static bool InfrastructureRequired =>
        string.Equals(Environment.GetEnvironmentVariable("METHODCACHE_REQUIRE_INFRA_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
}

[CollectionDefinition(Name)]
public sealed class ToxiproxyCollection : ICollectionFixture<ToxiproxyFixture>
{
    public const string Name = "Toxiproxy";
}
