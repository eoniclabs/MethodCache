using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Policies;
using Microsoft.Extensions.Configuration;
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
        await cache.GetOrCreateAsync(Canary, _ => ValueTask.FromResult(7), new CacheEntryOptions.Builder().AddTags(Canary).Build());
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
        Assert.Equal(7, await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync(Canary, _ => ValueTask.FromResult(7),
            new CacheEntryOptions.Builder().AddTags(Canary).Build()));
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
        await provider.GetRequiredService<ICacheManager>().GetOrCreateAsync("key", _ =>
        {
            Assert.Equal("tenant-a", Activity.Current!.GetBaggageItem("app.tenant"));
            Assert.Null(Activity.Current.GetBaggageItem("cache.user_id"));
            return ValueTask.FromResult(7);
        });
        Assert.Same(parent, Activity.Current);
        Assert.Equal("tenant-a", parent.GetBaggageItem("app.tenant"));
        Assert.Single(parent.Baggage);
    }

    [Fact]
    public async Task InvalidConfigurationReload_LogsOnlyTheErrorType_AndRetainsPreviousPolicy()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Methods:Sample.Read:Duration"] = "00:01:00"
        }).Build();
        var logs = new RecordingLogs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddMethodCache();
        services.AddMethodCache(methodCache => methodCache.FromConfiguration(configuration));
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPolicyRegistry>();
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy("Sample.Read").Policy.Duration);
        configuration["MethodCache:Methods:Sample.Read:Duration"] = Canary;
        configuration.Reload();
        var message = Assert.Single(logs.Messages);
        Assert.DoesNotContain(Canary, message);
        Assert.Contains(typeof(InvalidOperationException).FullName!, message);
        Assert.Equal(TimeSpan.FromMinutes(1), registry.GetPolicy("Sample.Read").Policy.Duration);
    }

    [Fact]
    public async Task FailedPolicyWatcher_DoesNotLogExceptionPayload()
    {
        var source = new FailingPolicySource();
        var logs = new RecordingLogs();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var resolver = new PolicyResolver([new PolicySourceRegistration(source, 1)], factory.CreateLogger<PolicyResolver>());
        _ = await resolver.ResolveAsync("Sample.Read");
        // Initialization schedules the watcher; resolving a policy does not mean it has started.
        await source.Watching.Task.WaitAsync(TimeSpan.FromSeconds(30));
        source.Fail(new InvalidOperationException(Canary, new Exception(Canary)));
        await logs.FirstMessage.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var message = Assert.Single(logs.Messages);
        Assert.DoesNotContain(Canary, message);
        Assert.Contains(typeof(InvalidOperationException).FullName!, message);
    }

    private sealed class FailingPolicySource : IPolicySource
    {
        // Once the watcher is waiting, fault delivery runs inline rather than depending on another
        // thread-pool dispatch while the signature corpus is compiling in other framework processes.
        private readonly Channel<PolicyChange> _changes = Channel.CreateUnbounded<PolicyChange>(new UnboundedChannelOptions
        { SingleReader = true, AllowSynchronousContinuations = true });
        internal TaskCompletionSource Watching { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SourceId => "fault-injection";
        public Task<IReadOnlyCollection<PolicySnapshot>> GetSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<PolicySnapshot>>([]);
        public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        {
            Watching.TrySetResult();
            return _changes.Reader.ReadAllAsync(cancellationToken);
        }
        public void Fail(Exception error) => _changes.Writer.TryComplete(error);
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal TaskCompletionSource<bool> FirstMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => new Sink(Messages, FirstMessage);
        public void Dispose() { }
        private sealed class Sink(ConcurrentQueue<string> messages, TaskCompletionSource<bool> firstMessage) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, error) + error?.ToString());
                firstMessage.TrySetResult(true);
            }
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
