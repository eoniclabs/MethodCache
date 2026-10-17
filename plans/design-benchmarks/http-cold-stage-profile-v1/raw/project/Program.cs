using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Security.Cryptography;
using System.Text.Json;
using MethodCache.HttpCaching;
using MethodCache.Benchmarks.Microbenchmarks;

var listening = bool.Parse(args[0]);
var invalidation = bool.Parse(args[1]);
var samples = new List<object>();
for (var batch = 0; batch < 13; batch++)
{
    var benchmark = new HttpColdMissBenchmarks { Listening = listening, AfterInvalidation = invalidation };
    await benchmark.Setup();
    try
    {
        var workload = typeof(HttpColdMissBenchmarks).GetField("_workload", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(benchmark)!;
        var client = (HttpClient)workload.GetType().GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workload)!;
        var handler = (HttpMessageHandler)typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
        while (handler.GetType().Name != "HttpCacheHandler")
            handler = handler is DelegatingHandler outer ? outer.InnerHandler! : throw new InvalidOperationException("Caching handler not found.");
        var storageField = handler.GetType().GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var counter = new Counters();
        storageField.SetValue(handler, new ProbeStorage((IHttpCacheStorage)storageField.GetValue(handler)!, counter));
        handler.GetType().GetField("_binding", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(handler, null);
        var warmLoads = benchmark.OriginLoads;
        var started = Stopwatch.GetTimestamp();
        var checksum = 0;
        for (var i = 0; i < HttpColdMissBenchmarks.InvocationsPerIteration; i++) checksum += await benchmark.Miss();
        var ticks = Stopwatch.GetTimestamp() - started;
        if (checksum != 200 * HttpColdMissBenchmarks.InvocationsPerIteration || benchmark.OriginLoads != warmLoads + HttpColdMissBenchmarks.InvocationsPerIteration)
            throw new InvalidOperationException("Cold publication/origin counts changed.");
        if (counter.Calls[0] != HttpColdMissBenchmarks.InvocationsPerIteration || counter.Calls[3] != HttpColdMissBenchmarks.InvocationsPerIteration)
            throw new InvalidOperationException("Storage proxy did not intercept every generation and publication.");
        if (batch >= 3)
            samples.Add(new
            {
                batch, operations = HttpColdMissBenchmarks.InvocationsPerIteration, checksum,
                originLoads = benchmark.OriginLoads - warmLoads,
                totalNsPerOperation = ticks * 1e9 / Stopwatch.Frequency / HttpColdMissBenchmarks.InvocationsPerIteration,
                buckets = Enumerable.Range(0, 4).Select(i => new
                {
                    name = new[] { "generation", "lookup", "bodyBudget", "publication" }[i],
                    callsPerOperation = counter.Calls[i] / (double)HttpColdMissBenchmarks.InvocationsPerIteration,
                    nsPerOperation = counter.Ticks[i] * 1e9 / Stopwatch.Frequency / HttpColdMissBenchmarks.InvocationsPerIteration
                }).ToArray()
            });
    }
    finally { benchmark.Cleanup(); }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    scope = "Diagnostic instrumented storage-phase buckets; three warm batches and ten measured bounded8192-operation batches. NOT paired-time acceptance; original binaries and workload retained, proxy timing overhead present.",
    listening, afterInvalidation = invalidation, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    serverGC = GCSettings.IsServerGC, gcLatencyMode = GCSettings.LatencyMode.ToString(),
    loadedLibraries = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("MethodCache", StringComparison.Ordinal) && !a.IsDynamic)
        .Select(a => new { name = a.GetName().Name, location = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray(),
    samples
}));

sealed class Counters
{
    internal readonly long[] Calls = new long[4];
    internal readonly long[] Ticks = new long[4];
    internal void Add(int bucket, long start) { Ticks[bucket] += Stopwatch.GetTimestamp() - start; Calls[bucket]++; }
}

sealed class ProbeStorage(IHttpCacheStorage inner, Counters counts) : IHttpCacheStorage
{
    public ValueTask<string> GetGenerationAsync(string key, CancellationToken token = default)
    {
        var start = Stopwatch.GetTimestamp(); var pending = inner.GetGenerationAsync(key, token);
        if (!pending.IsCompletedSuccessfully) return Finish(pending, 0, start);
        var value = pending.Result; counts.Add(0, start); return new(value);
    }
    public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken token = default)
    {
        var start = Stopwatch.GetTimestamp(); var pending = inner.GetAsync(key, token);
        if (!pending.IsCompletedSuccessfully) return Finish(pending, 1, start);
        var value = pending.Result; counts.Add(1, start); return new(value);
    }
    public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan ttl, CancellationToken token = default)
    {
        var start = Stopwatch.GetTimestamp(); var pending = inner.SetAsync(key, entry, ttl, token);
        if (!pending.IsCompletedSuccessfully) return Finish(pending, 3, start);
        pending.GetAwaiter().GetResult(); counts.Add(3, start); return ValueTask.CompletedTask;
    }
#if HEAD
    public IHttpCacheStorage ForClient(string client, HttpCacheOptions options) => new ProbeStorage(inner.ForClient(client, options), counts);
    public long BodyBudget(string key, HttpCacheEntry metadata)
    {
        var start = Stopwatch.GetTimestamp(); var value = inner.BodyBudget(key, metadata); counts.Add(2, start); return value;
    }
    public ValueTask<bool> TrySetAsync(string key, HttpCacheEntry entry, TimeSpan ttl, CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp(); var pending = inner.TrySetAsync(key, entry, ttl, token);
        if (!pending.IsCompletedSuccessfully) return Finish(pending, 3, start);
        var value = pending.Result; counts.Add(3, start); return new(value);
    }
    public ValueTask InvalidateManyAsync(IReadOnlyList<string> keys, CancellationToken token) => inner.InvalidateManyAsync(keys, token);
#endif
    private async ValueTask<T> Finish<T>(ValueTask<T> pending, int bucket, long start)
    { try { return await pending.ConfigureAwait(false); } finally { counts.Add(bucket, start); } }
    private async ValueTask Finish(ValueTask pending, int bucket, long start)
    { try { await pending.ConfigureAwait(false); } finally { counts.Add(bucket, start); } }
    public ValueTask InvalidateAsync(string key, CancellationToken token = default) => inner.InvalidateAsync(key, token);
    public ValueTask RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
    public ValueTask ClearAsync(CancellationToken token = default) => inner.ClearAsync(token);
}
