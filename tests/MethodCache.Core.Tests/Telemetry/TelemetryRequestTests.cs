using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Telemetry;

public class TelemetryRequestTests
{
    [Fact]
    public async Task BulkCall_RecordsOneOutcomeAndOneActualBatchOrigin()
    {
        var clock = new FakeTimeProvider();
        using var provider = Node(clock);
        using var recorded = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateManyAsync<int>(["a", "b"], (keys, _, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(2));
            return ValueTask.FromResult<IDictionary<string, int>>(keys.ToDictionary(key => key, _ => 7));
        });
        await cache.GetOrCreateManyAsync<int>(["a", "b"], (_, _, _) => throw new Exception("must hit"));
        await cache.GetOrCreateManyAsync<int>(["c"], (keys, _, _) => ValueTask.FromResult<IDictionary<string, int>>(keys.ToDictionary(key => key, _ => 8)), b => b.WithEnabled(false));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetOrCreateManyAsync<int>(["d"], (_, _, _) => throw new InvalidOperationException("failed")));
        Assert.Equal(new[] { "bypass", "error", "hit", "miss" }, recorded.Requests.Order(StringComparer.Ordinal));
        Assert.Equal(3, recorded.Loads.Count);
        Assert.Contains(recorded.Loads, row => row.Value == 0.002 && Equals(row.Tags["name"], "MethodCache.Fluent.Bulk"));
    }

    [Fact]
    public async Task GeneratedDisabledAndNoOpCalls_AreBypasses_AndStillMeasureOrigins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITelemetryShapes, TelemetryShapes>();
        services.AddSingleton<ICacheManager, NoOpCacheManager>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);
        var shapes = provider.GetRequiredService<ITelemetryShapes>();
        Assert.Equal(7, await shapes.ReadAsync(7));
        Assert.Equal(7, await shapes.ReadAsync(7));
        Assert.Equal(8, shapes.Disabled(8));
        Assert.Equal(9, await shapes.DisabledAsync(9));
        Assert.Equal(10, await shapes.DisabledValueAsync(10));
        Assert.Equal(5, recorded.Requests.Count());
        Assert.All(recorded.Requests, result => Assert.Equal("bypass", result));
        Assert.Equal(5, recorded.Loads.Count);
    }

    [Fact]
    public async Task GeneratedSyncTaskAndValueTask_CountBothMissAndFastHit()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITelemetryShapes, TelemetryShapes>();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        using var recorded = new TelemetryRecorder(provider);
        var shapes = provider.GetRequiredService<ITelemetryShapes>();
        Assert.Equal(7, shapes.Read(7));
        Assert.Equal(7, shapes.Read(7));
        Assert.Equal(8, await shapes.ReadAsync(8));
        Assert.Equal(8, await shapes.ReadAsync(8));
        Assert.Equal(9, await shapes.ReadValueAsync(9));
        Assert.Equal(9, await shapes.ReadValueAsync(9));
        Assert.Equal(new[] { "hit", "hit", "hit", "miss", "miss", "miss" }, recorded.Requests.Order(StringComparer.Ordinal));
        Assert.Equal(3, recorded.Loads.Count);
    }

    [Fact]
    public async Task Refresh_RecordsAnOriginDuration_WithoutAnotherRequest()
    {
        var clock = new FakeTimeProvider();
        using var provider = Node(clock);
        using var recorded = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();
        var options = new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromSeconds(30)).RefreshAhead(TimeSpan.FromSeconds(10)).Build();
        await cache.GetOrCreateAsync("refresh", (_, _) => ValueTask.FromResult(7), options);
        clock.Advance(TimeSpan.FromSeconds(21));
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.Equal(7, await cache.GetOrCreateAsync("refresh", (_, _) => { began.TrySetResult(); return new ValueTask<int>(release.Task); }, options));
        await began.Task.WaitAsync(TimeSpan.FromSeconds(30));
        release.SetResult(8);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (Assert.IsType<InMemoryCacheManager>(cache).IsLoading("refresh"))
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        Assert.Equal(new[] { "hit", "miss" }, recorded.Requests.Order(StringComparer.Ordinal));
        Assert.Equal(2, recorded.Loads.Count);
    }

    [Fact]
    public async Task PublicCalls_RecordExactlyOneFinalOutcome_AndOnlyActualOriginDurations()
    {
        using var provider = Node(new FakeTimeProvider());
        using var recorded = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();
        Assert.Equal(7, await cache.GetOrCreateAsync("key", (_, _) => ValueTask.FromResult(7)));
        Assert.Equal(7, await cache.GetOrCreateAsync<int>("key", (_, _) => throw new InvalidOperationException("must hit")));
        Assert.True((await cache.TryGetAsync<int>("key")).Found);
        Assert.False((await cache.TryGetAsync<int>("missing")).Found);
        var disabled = new CacheEntryOptions.Builder().WithEnabled(false).Build();
        Assert.Equal(9, await cache.GetOrCreateAsync("bypass", (_, _) => ValueTask.FromResult(9), disabled));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await cache.GetOrCreateAsync<int>("error", (_, _) => throw new InvalidOperationException("source failed")));
        Assert.Equal(new[] { "bypass", "error", "hit", "hit", "miss", "miss" }, recorded.Requests.Order(StringComparer.Ordinal));
        Assert.Equal(3, recorded.Loads.Count);
    }

    [Fact]
    public async Task FlightJoin_CountsOneOriginAndOneOutcomeForEachCaller()
    {
        using var provider = Node(new FakeTimeProvider());
        using var recorded = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetOrCreateAsync("flight", (_, _) => new ValueTask<int>(release.Task));
        var joined = cache.GetOrCreateAsync<int>("flight", (_, _) => throw new InvalidOperationException("must join"));
        release.SetResult(42);
        Assert.Equal(42, await first);
        Assert.Equal(42, await joined);
        Assert.Equal(new[] { "hit", "miss" }, recorded.Requests.Order(StringComparer.Ordinal));
        Assert.Single(recorded.Loads);
    }

    [Fact]
    public async Task L2Hit_IsAHitWithoutAnOriginDuration()
    {
        var clock = new FakeTimeProvider();
        var shared = new MethodCache.TestSupport.AtomicTestCache(clock);
        using var a = Node(clock, shared);
        using var b = Node(clock, shared);
        await a.GetRequiredService<ICacheManager>().GetOrCreateAsync("l2", (_, _) => ValueTask.FromResult(7));
        using var recorded = new TelemetryRecorder(b);
        Assert.Equal(7, await b.GetRequiredService<ICacheManager>().GetOrCreateAsync<int>("l2", (_, _) => throw new InvalidOperationException("must read L2")));
        Assert.Equal("hit", Assert.Single(recorded.Requests));
        Assert.Empty(recorded.Loads);
    }

    [Fact]
    public async Task StaleFallback_EndsTheRequestAsStale_AndTheOriginAsError()
    {
        var clock = new FakeTimeProvider();
        using var provider = Node(clock);
        using var recorded = new TelemetryRecorder(provider);
        var cache = provider.GetRequiredService<ICacheManager>();
        var options = new CacheEntryOptions.Builder().WithDuration(TimeSpan.FromSeconds(1)).FailSafe(TimeSpan.FromMinutes(1)).Build();
        await cache.GetOrCreateAsync("stale", (_, _) => ValueTask.FromResult(7), options);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(7, await cache.GetOrCreateAsync<int>("stale", (_, _) => throw new InvalidOperationException("canary-origin-error"), options));
        Assert.Equal(7, await cache.GetOrCreateAsync<int>("stale", (_, _) => throw new InvalidOperationException("throttle must serve stale"), options));
        Assert.Equal(new[] { "miss", "stale", "stale" }, recorded.Requests.Order(StringComparer.Ordinal));
        Assert.Equal(2, recorded.Loads.Count);
        Assert.Contains(recorded.Loads, row => Equals(row.Tags.GetValueOrDefault("error.type"), typeof(InvalidOperationException).FullName));
    }

    [Fact]
    public async Task LocalInvalidation_ReportsTargetsPerStage()
    {
        using var provider = Node(new FakeTimeProvider());
        using var recorded = new TelemetryRecorder(provider);
        var result = await provider.GetRequiredService<ICacheManager>().InvalidateKeysAsync(["a", "a", "b"]);
        Assert.Equal(2, result.Local);
        Assert.Contains(recorded.Longs, row => row.Name == "methodcache.invalidations" && row.Value == 2 && Equals(row.Tags["type"], "key") && Equals(row.Tags["stage"], "local"));
    }

    private static ServiceProvider Node(FakeTimeProvider time, MethodCache.TestSupport.AtomicTestCache? backend = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddMethodCache();
        if (backend != null) services.AddMethodCacheDistributedCache(_ => backend, options => options.ClockSkewTolerance = TimeSpan.Zero);
        return services.BuildServiceProvider();
    }
}

internal sealed class TelemetryRecorder : IDisposable
{
    private readonly MeterListener _listener;
    internal ConcurrentQueue<(string Name, long Value, Dictionary<string, object?> Tags)> Longs { get; } = new();
    internal ConcurrentQueue<(double Value, Dictionary<string, object?> Tags)> Loads { get; } = new();
    internal ConcurrentQueue<(string Operation, double Value, Dictionary<string, object?> Tags)> Distributed { get; } = new();
    internal void Poll() => _listener.RecordObservableInstruments();
    internal IEnumerable<string> Requests => Longs.Where(row => row.Name == "methodcache.requests").Select(row => (string)row.Tags["result"]!);
    internal TelemetryRecorder(IServiceProvider provider)
    {
        var factory = provider.GetRequiredService<IMeterFactory>();
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) => { if (ReferenceEquals(instrument.Meter.Scope, factory)) listener.EnableMeasurementEvents(instrument); }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Longs.Enqueue((instrument.Name, value, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value))));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "methodcache.load.duration") Loads.Enqueue((value, tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value)));
            if (instrument.Name == "methodcache.distributed.duration")
            {
                var data = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value);
                Distributed.Enqueue(((string)data["operation"]!, value, data));
            }
        });
        _listener.Start();
    }
    public void Dispose() => _listener.Dispose();
}

public interface ITelemetryShapes
{
    [Cache] int Read(int id);
    [Cache] Task<int> ReadAsync(int id);
    [Cache] ValueTask<int> ReadValueAsync(int id);
    [Cache(Enabled = false)] int Disabled(int id);
    [Cache(Enabled = false)] Task<int> DisabledAsync(int id);
    [Cache(Enabled = false)] ValueTask<int> DisabledValueAsync(int id);
}
public sealed class TelemetryShapes : ITelemetryShapes
{
    public int Read(int id) => id;
    public Task<int> ReadAsync(int id) => Task.FromResult(id);
    public ValueTask<int> ReadValueAsync(int id) => ValueTask.FromResult(id);
    public int Disabled(int id) => id;
    public Task<int> DisabledAsync(int id) => Task.FromResult(id);
    public ValueTask<int> DisabledValueAsync(int id) => ValueTask.FromResult(id);
}
