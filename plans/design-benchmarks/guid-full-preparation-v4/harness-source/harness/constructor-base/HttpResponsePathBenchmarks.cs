using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Microbenchmarks;

// Uses the common public API so the exact workload can also be built against the PR's merge-base.
[MemoryDiagnoser]
public class HttpResponsePathBenchmarks
{
    private ServiceProvider _provider = null!;
    private HttpClient _client = null!;
    private Origin _origin = null!;
    private MeterListener? _listener;
    private int _next;
    [Params(false, true)] public bool Listening { get; set; }
    [Params(false, true)] public bool AfterInvalidation { get; set; }
    public int OriginLoads => _origin.Calls;
    internal IServiceProvider Services => _provider;

    [GlobalSetup]
    public async Task Setup()
    {
        if (Listening)
        {
            _listener = new MeterListener();
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "MethodCache") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            _listener.Start();
        }
        _origin = new Origin();
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.AddHttpClient("http-benchmark").AddHttpCaching(o => o.AllowUnverifiedTransport = true)
            .ConfigurePrimaryHttpMessageHandler(() => _origin);
        _provider = services.BuildServiceProvider();
        _client = _provider.GetRequiredService<IHttpClientFactory>().CreateClient("http-benchmark");
        await FreshHit(); await VariantHit(); await Revalidated();
        if (_origin.Calls != 3) throw new InvalidOperationException("HTTP setup did not populate all representations.");
        if (AfterInvalidation)
        {
            using (var freshWrite = await _client.PostAsync("https://benchmark.invalid/fresh", new StringContent("update"))) { }
            using (var variantWrite = await _client.PostAsync("https://benchmark.invalid/variant", new StringContent("update"))) { }
            await FreshHit(); await VariantHit();
            if (_origin.Calls != 7) throw new InvalidOperationException("HTTP setup did not prove post-invalidation origin reloads.");
        }
    }

    [Benchmark] public Task<int> FreshHit() => Read("https://benchmark.invalid/fresh");
    [Benchmark] public Task<int> VariantHit() => Read("https://benchmark.invalid/variant", caller: true);
    [Benchmark] public Task<int> Revalidated() => Read("https://benchmark.invalid/revalidated");
    [Benchmark] public Task<int> Miss() => Read("https://benchmark.invalid/miss/" + ++_next);

    private async Task<int> Read(string url, bool caller = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (caller) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "fixed-benchmark-caller");
        using var response = await _client.SendAsync(request).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.Dispose(); _provider.Dispose(); _listener?.Dispose();
    }

    private sealed class Origin : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            var validates = request.RequestUri!.AbsolutePath == "/revalidated";
            var response = new HttpResponseMessage(validates && request.Headers.IfNoneMatch.Count != 0 ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            {
                Content = new StringContent("fixed body"),
                Headers =
                {
                    CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1), NoCache = validates },
                    ETag = new EntityTagHeaderValue("\"fixed\"")
                }
            };
            return Task.FromResult(response);
        }
    }
}

// Publication without an ever-growing resident set. Setup/teardown are outside the measured operation;
// every iteration starts with the same three warm representations and at most 8192 new responses.
// The older body's-only budget and the new metadata-inclusive budget both comfortably admit this batch.
[MemoryDiagnoser]
[InvocationCount(HttpColdMissBenchmarks.InvocationsPerIteration, unrollFactor: 1)]
public class HttpColdMissBenchmarks
{
    public const int InvocationsPerIteration = 8192;
    private HttpResponsePathBenchmarks? _workload;
    [Params(false, true)] public bool Listening { get; set; }
    [Params(false, true)] public bool AfterInvalidation { get; set; }
    public int OriginLoads => _workload!.OriginLoads;
    internal IServiceProvider Services => _workload!.Services;

    [GlobalSetup] public Task Setup() => ResetBatch();

    [IterationSetup]
    public async Task ResetBatch()
    {
        _workload?.Cleanup();
        _workload = new HttpResponsePathBenchmarks { Listening = Listening, AfterInvalidation = AfterInvalidation };
        await _workload.Setup().ConfigureAwait(false);
    }

    [Benchmark] public Task<int> Miss() => _workload!.Miss();

    [GlobalCleanup]
    public void Cleanup()
    {
        _workload?.Cleanup();
        _workload = null;
    }
}
