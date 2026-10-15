using System.Collections.Concurrent;
using System.Diagnostics;
using MethodCache.Core.Telemetry;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MethodCache.Core.Tests.Telemetry;

public class TelemetryPrivacyTests
{
    private const string Canary = "alice@example.test?token=secret-canary";

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task AllOperationSpans_RespectExplicitIdentityOptIns_WithoutMetricDimensions(bool keys, bool hash, bool tags)
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        services.Configure<MethodCacheTelemetryOptions>(options =>
        {
            options.RecordCacheKeys = keys;
            options.HashCacheKeys = hash;
            options.RecordCacheTags = tags;
        });
        using var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<CoreTelemetry>();
        using var metrics = new TelemetryRecorder(provider);
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var cache = provider.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateAsync(Canary, (_, _) => ValueTask.FromResult(7), new CacheEntryOptions.Builder().WithTags(Canary).Build());
        await cache.InvalidateKeysAsync([Canary]);
        await cache.InvalidateTagsAsync([Canary]);
        await provider.GetRequiredService<ICacheAdministrator>().ClearAsync();
        Assert.Equal(4, spans.Count);
        var values = spans.SelectMany(activity => activity.TagObjects).SelectMany(pair => Strings(pair.Value)).ToArray();
        Assert.Equal((keys && !hash) || tags, values.Any(value => value.Contains(Canary, StringComparison.Ordinal)));
        if (keys) Assert.Contains(spans, activity => activity.GetTagItem("methodcache.keys") != null);
        if (tags) Assert.Contains(spans, activity => activity.GetTagItem("methodcache.tags") != null);
        Assert.All(metrics.Longs, row => Assert.All(row.Tags, tag => Assert.DoesNotContain(Canary, tag.Value?.ToString() ?? "")));
    }

    private static IEnumerable<string> Strings(object? value)
    {
        if (value is string text) yield return text;
        else if (value is IEnumerable<string> texts) foreach (var item in texts) yield return item;
    }

    [Fact]
    public async Task BackendFailures_DoNotLeakExceptionMessagesIntoDefaultLogsSpansOrMetrics()
    {
        var logs = new RecordingLogs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => new FailingBackend());
        using var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<CoreTelemetry>();
        using var metrics = new TelemetryRecorder(provider);
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => spans.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("request").SetTag("url.full", Canary).Start();
        Assert.Equal(7, await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync(Canary, (_, _) => ValueTask.FromResult(7),
            new CacheEntryOptions.Builder().WithTags(Canary).Build()));
        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, message => Assert.DoesNotContain(Canary, message));
        Assert.NotEmpty(spans);
        Assert.All(spans, span =>
        {
            Assert.DoesNotContain(Canary, span.DisplayName);
            Assert.DoesNotContain(Canary, span.StatusDescription ?? "");
            Assert.All(span.TagObjects, tag => Assert.DoesNotContain(Canary, tag.Value?.ToString() ?? ""));
            Assert.Null(span.GetTagItem("url.full"));
            Assert.Null(span.GetTagItem("exception.message"));
            Assert.Equal(parent.TraceId, span.TraceId);
        });
        Assert.All(metrics.Longs, row => Assert.All(row.Tags, tag => Assert.DoesNotContain(Canary, tag.Value?.ToString() ?? "")));
    }

    [Fact]
    public async Task Instrumentation_DoesNotChangeTheApplicationsAmbientBaggage()
    {
        using var provider = new ServiceCollection().AddMethodCache().BuildServiceProvider();
        var telemetry = provider.GetRequiredService<CoreTelemetry>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, telemetry.Source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("request").AddBaggage("app.tenant", "tenant-a").Start();
        await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync("key", (_, _) =>
        {
            Assert.Equal("tenant-a", Activity.Current!.GetBaggageItem("app.tenant"));
            Assert.Null(Activity.Current.GetBaggageItem("cache.user_id"));
            return ValueTask.FromResult(7);
        });
        Assert.Same(parent, Activity.Current);
        Assert.Equal("tenant-a", parent.GetBaggageItem("app.tenant"));
        Assert.Single(parent.Baggage);
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Messages);
        public void Dispose() { }
        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, error) + error?.ToString());
        }
    }

    private sealed class FailingBackend : IAtomicInvalidationStore
    {
        public byte[]? Get(string key) => throw new InvalidOperationException(Canary);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException(Canary);
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => throw new InvalidOperationException(Canary);
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException(Canary);
        public void Refresh(string key) => throw new InvalidOperationException(Canary);
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException(Canary);
        public void Remove(string key) => throw new InvalidOperationException(Canary);
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException(Canary);
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default) => throw new InvalidOperationException(Canary);
    }
}
