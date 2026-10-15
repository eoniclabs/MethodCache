using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MethodCache.Benchmarks.Microbenchmarks;

// Only common public calls occur in measured code. Setup reflection accommodates renamed pre-1.0 options
// so these exact sources can be built against every revision, including f43854d.
internal static class DesignWorkloadSetup
{
    internal static void PreferPerformanceCores()
    {
        if (OperatingSystem.IsMacOS() && SetQos(0x19, 0) != 0)
            throw new InvalidOperationException("Could not select benchmark thread QoS.");
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static extern int SetQos(uint quality, int priority);

    internal static void SetWeightLimit(MethodCacheMemoryOptions options, long limit)
        => (typeof(MethodCacheMemoryOptions).GetProperty("SizeLimit") ?? typeof(MethodCacheMemoryOptions).GetProperty("MaxMemoryBytes")
            ?? throw new InvalidOperationException("No supported weight limit.")).SetValue(options, limit);

    internal static MeterListener? Listen(IServiceCollection services, bool enabled)
    {
        if (!enabled) return null;
        // Before Core owned instrumentation, the add-on installed the instrumented manager. Do that only
        // where this public registration exists; current Core instruments every manager itself.
        var telemetry = Assembly.Load("MethodCache.OpenTelemetry");
        var registration = telemetry.GetTypes().SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .SingleOrDefault(method => method.Name == "AddMethodCacheOpenTelemetry");
        registration?.Invoke(null, [services, null]);
        var listener = new MeterListener
        {
            InstrumentPublished = static (instrument, self) =>
            {
                if (instrument.Meter.Name.StartsWith("MethodCache", StringComparison.Ordinal)) self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();
        return listener;
    }

    internal static void Backend(IServiceCollection services, string backend, string prefix, bool roundTrip)
    {
        if (backend == "Local") return;
        if (backend == "Redis")
            services.AddMethodCacheRedis(options =>
            {
                options.Configuration = Environment.GetEnvironmentVariable("METHODCACHE_REDIS_URL")
                    ?? throw new InvalidOperationException("A real Redis is required.");
                options.EnableBackplane = false;
            });
        else if (backend == "SqlServer")
            services.AddMethodCacheSqlServer(options =>
            {
                options.ConnectionString = Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_CONNECTION")
                    ?? throw new InvalidOperationException("A real SQL Server is required.");
                options.SchemaName = prefix.Replace(":", "", StringComparison.Ordinal);
                options.EnableBackplane = false;
            });
        else throw new ArgumentOutOfRangeException(nameof(backend));
        // Register last: pre-PR4 provider registration also writes the Core prefix option.
        services.Configure<DistributedCacheOptions>(options =>
        {
            options.KeyPrefix = prefix;
            options.OperationTimeout = TimeSpan.FromSeconds(5);
            // This feature was added in PR 4. Earlier revisions have their existing round-trip behavior.
            var property = typeof(DistributedCacheOptions).GetProperty("VerifyRoundTrip");
            if (property != null) property.SetValue(options, roundTrip);
        });
    }

    internal static async Task StartAsync(ServiceProvider provider)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var service in provider.GetServices<IHostedService>())
        {
            await service.StartAsync(timeout.Token);
            // Wait for actual table creation, rather than letting startup's bounded wait become an L2 bypass.
            if (service.GetType().GetProperty("Creating", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(service) is Task creating)
                await creating.WaitAsync(timeout.Token);
        }
        if (provider.GetKeyedService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>("MethodCache") is { } backend)
            _ = await backend.GetAsync("design-readiness", timeout.Token);
    }
}

[MemoryDiagnoser]
public class DesignMemoryWorkloadBenchmarks
{
    private ServiceProvider _provider = null!;
    private ICacheManager _cache = null!;
    private MeterListener? _listener;
    private int _next;
    public int OriginLoads { get; private set; }
    [Params(false, true)] public bool Listening { get; set; }
    [Params(false, true)] public bool Weighted { get; set; }

    public bool SelectPerformanceCores { get; set; } = true;

    [GlobalSetup]
    public async Task Setup()
    {
        if (SelectPerformanceCores) DesignWorkloadSetup.PreferPerformanceCores();
        var services = new ServiceCollection();
        services.Configure<MethodCacheMemoryOptions>(options =>
        {
            options.MaxItems = 32;
            if (Weighted) { options.Weigher = static (_, _) => 32; DesignWorkloadSetup.SetWeightLimit(options, 1024); }
        });
        services.AddMethodCache();
        _listener = DesignWorkloadSetup.Listen(services, Listening);
        _provider = services.BuildServiceProvider();
        _cache = _provider.GetRequiredService<ICacheManager>();
        for (var i = 0; i < 32; i++) _ = await _cache.GetOrCreateAsync("warm:" + i, static (_, _) => ValueTask.FromResult(42));
        _ = await Hit();
    }

    [Benchmark] public ValueTask<int> Hit() => _cache.GetOrCreateAsync<int>("warm:0", static (_, _) => throw new InvalidOperationException("Expected a resident hit."));
    [Benchmark] public ValueTask<int> EvictionPressure() => _cache.GetOrCreateAsync("pressure:" + Interlocked.Increment(ref _next), static (_, _) => ValueTask.FromResult(42));

    [Benchmark(OperationsPerInvoke = 256)]
    public Task ContendedHits() => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
    {
        for (var i = 0; i < 64; i++) _ = await Hit();
    })));

    [Benchmark]
    public async Task<int> SingleFlightBatch()
    {
        var key = "flight:" + Interlocked.Increment(ref _next);
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = new Task<int>[4];
        for (var i = 0; i < arrivals.Length; i++)
            arrivals[i] = _cache.GetOrCreateAsync(key, (_, _) =>
            {
                OriginLoads++;
                return new ValueTask<int>(source.Task);
            }).AsTask();
        source.SetResult(42);
        var values = await Task.WhenAll(arrivals);
        return values.Sum();
    }

    [GlobalCleanup] public void Cleanup() { _provider?.Dispose(); _listener?.Dispose(); }
}

[MemoryDiagnoser]
public class DesignL2WorkloadBenchmarks
{
    private ServiceProvider _provider = null!;
    private ICacheManager _cache = null!;
    private int _next;
    public int OriginLoads { get; private set; }
    [Params("Redis", "SqlServer")] public string Backend { get; set; } = "Redis";
    [Params(false, true)] public bool RoundTrip { get; set; }

    public bool SelectPerformanceCores { get; set; } = true;

    [GlobalSetup]
    public async Task Setup()
    {
        if (SelectPerformanceCores) DesignWorkloadSetup.PreferPerformanceCores();
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.Configure<MethodCacheMemoryOptions>(options =>
        {
            // Reject L1 admission while preserving the actual method L2 read/publication path.
            // No TTL advance, invalidation or new container is included in a measured read.
            options.Weigher = static (_, _) => 2048;
            DesignWorkloadSetup.SetWeightLimit(options, 1024);
        });
        DesignWorkloadSetup.Backend(services, Backend, "design_" + Guid.NewGuid().ToString("N"), RoundTrip);
        _provider = services.BuildServiceProvider();
        await DesignWorkloadSetup.StartAsync(_provider);
        _cache = _provider.GetRequiredService<ICacheManager>();
        _ = await _cache.GetOrCreateAsync("l2-hit", (_, _) => { OriginLoads++; return ValueTask.FromResult(42); });
        _ = await Read(); _ = await Read();
        if (OriginLoads != 1) throw new InvalidOperationException($"Setup did not establish repeated real L2 hits (origin loads: {OriginLoads}).");
    }

    [Benchmark]
    public ValueTask<int> Read() => _cache.GetOrCreateAsync<int>("l2-hit", (_, _) =>
    {
        OriginLoads++;
        throw new InvalidOperationException("An L2 read unexpectedly ran its origin.");
    });

    [Benchmark]
    public ValueTask<int> Write() => _cache.GetOrCreateAsync("l2-write:" + ++_next, (_, _) =>
    {
        OriginLoads++;
        return ValueTask.FromResult(42);
    });

    [GlobalCleanup] public async Task Cleanup() { if (_provider != null) await _provider.DisposeAsync(); }
}

[MemoryDiagnoser]
public class DesignHttpWorkloadBenchmarks
{
    private ServiceProvider _provider = null!;
    private HttpClient _client = null!;
    private Origin _origin = null!;
    private int _next;
    [Params("Local", "Redis", "SqlServer")] public string Backend { get; set; } = "Local";
    [Params(false, true)] public bool KeepCookies { get; set; }
    public int OriginLoads => _origin.Loads;

    public bool SelectPerformanceCores { get; set; } = true;

    [GlobalSetup]
    public async Task Setup()
    {
        if (SelectPerformanceCores) DesignWorkloadSetup.PreferPerformanceCores();
        var services = new ServiceCollection();
        services.AddMethodCache();
        DesignWorkloadSetup.Backend(services, Backend, "design_http_" + Guid.NewGuid().ToString("N"), roundTrip: false);
        _origin = new Origin();
        var client = services.AddHttpClient("design-http");
        var keepCookies = typeof(MethodCache.HttpCaching.HttpCacheOptions).GetProperty("KeepCookies");
        // Before KeepCookies existed, use the same origin/HttpClient with caching absent for the preserved-cookie
        // baseline. This measures a bypass, not a claim that the previous response cache supported cookies.
        if (!KeepCookies || keepCookies != null)
            client.AddHttpCaching(options =>
            {
                options.AllowUnverifiedTransport = true;
                keepCookies?.SetValue(options, KeepCookies);
            });
        client.ConfigurePrimaryHttpMessageHandler(() => _origin);
        _provider = services.BuildServiceProvider();
        await DesignWorkloadSetup.StartAsync(_provider);
        _client = _provider.GetRequiredService<IHttpClientFactory>().CreateClient("design-http");
        await Fresh(); await Fresh();
        if (OriginLoads != (KeepCookies ? 2 : 1)) throw new InvalidOperationException("HTTP setup failed to prove the requested hit/bypass path.");
    }

    [Benchmark] public Task<int> Fresh() => Read("https://benchmark.invalid/fresh");
    [Benchmark] public Task<int> Miss() => Read("https://benchmark.invalid/miss/" + ++_next);

    private async Task<int> Read(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _client.SendAsync(request);
        return (int)response.StatusCode;
    }

    [GlobalCleanup]
    public async Task Cleanup() { _client?.Dispose(); if (_provider != null) await _provider.DisposeAsync(); }

    private sealed class Origin : HttpMessageHandler
    {
        internal int Loads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Loads);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("fixed body"),
                Headers = { CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) } }
            });
        }
    }
}
