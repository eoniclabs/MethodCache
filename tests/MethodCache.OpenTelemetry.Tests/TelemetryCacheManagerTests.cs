using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

public class TelemetryCacheManagerTests : IDisposable
{
    private readonly Mock<ICacheManager> _innerManagerMock;
    private readonly Mock<ICacheActivitySource> _activitySourceMock;
    private readonly Mock<ICacheMeterProvider> _meterProviderMock;
    private readonly Mock<IBaggagePropagator> _baggagePropagatorMock;
    private readonly TelemetryCacheManager _telemetryCacheManager;
    private readonly List<Activity> _exportedActivities;
    private readonly TracerProvider _tracerProvider;

    public TelemetryCacheManagerTests()
    {
        _innerManagerMock = new Mock<ICacheManager>();
        _activitySourceMock = new Mock<ICacheActivitySource>();
        _meterProviderMock = new Mock<ICacheMeterProvider>();
        _baggagePropagatorMock = new Mock<IBaggagePropagator>();

        _telemetryCacheManager = new TelemetryCacheManager(
            _innerManagerMock.Object,
            _activitySourceMock.Object,
            _meterProviderMock.Object,
            _baggagePropagatorMock.Object);

        _exportedActivities = new List<Activity>();
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(TracingConstants.ActivitySourceName)
            .AddInMemoryExporter(_exportedActivities)
            .Build()!;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TryGetAsync_RealCachedNull_ReportsHitWithExactlyOneRead(bool fastPath, bool refreshDue)
    {
        var time = new FakeTimeProvider();
        using var inner = new InMemoryCacheManager(new NullCacheMetricsProvider(), Options.Create(new MethodCacheMemoryOptions
        { EnableFastPath = fastPath, EnableBackgroundCleanup = false }), time);
        var policy = CacheRuntimePolicy.Empty("null") with { Duration = TimeSpan.FromSeconds(10), RefreshAhead = TimeSpan.FromSeconds(2) };
        var keys = new Mock<ICacheKeyGenerator>();
        keys.Setup(x => x.GenerateKey("null", It.IsAny<object[]>(), policy)).Returns("null-key");
        await inner.GetOrCreateFastAsync<string?>("null-key", "null", () => Task.FromResult<string?>(null), policy);
        if (refreshDue) time.Advance(TimeSpan.FromSeconds(9));
        using var activity = new Activity("real-null");
        _activitySourceMock.Setup(x => x.StartCacheOperation("null", TracingConstants.Operations.Get)).Returns(activity);
        var wrapper = new TelemetryCacheManager(inner, _activitySourceMock.Object, _meterProviderMock.Object, _baggagePropagatorMock.Object);
        var before = (await inner.GetStatsAsync()).Hits;
        Assert.Null(await wrapper.TryGetAsync<string?>("null", Array.Empty<object>(), policy, keys.Object));
        _activitySourceMock.Verify(x => x.SetCacheHit(activity, true), Times.Once);
        Assert.Equal(before, (await inner.GetStatsAsync()).Hits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TryGetAsync_NullReference_DistinguishesCachedNullFromMiss(bool stored) =>
        AssertNullHitAsync<string>(stored);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TryGetAsync_NullNullable_DistinguishesCachedNullFromMiss(bool stored) =>
        AssertNullHitAsync<int?>(stored);

    private async Task AssertNullHitAsync<T>(bool stored)
    {
        var policy = CacheRuntimePolicy.FromPolicy("test", Policies.CachePolicy.Empty, Policies.CachePolicyFields.None);
        var keys = new Mock<ICacheKeyGenerator>();
        keys.Setup(x => x.GenerateKey("null", It.IsAny<object[]>(), policy)).Returns("null-key");
        _innerManagerMock.Setup(x => x.TryGetAsync<T>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored ? CacheLookupResult<T>.Hit(default!) : CacheLookupResult<T>.Miss);
        using var activity = new Activity("cached-null");
        _activitySourceMock.Setup(x => x.StartCacheOperation("null", TracingConstants.Operations.Get)).Returns(activity);

        await _telemetryCacheManager.TryGetAsync<T>("null", Array.Empty<object>(), policy, keys.Object);

        _activitySourceMock.Verify(x => x.SetCacheHit(activity, stored), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_CacheHit_MarksSpanAsHitWithoutCountingIt()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1, "test" };
        var settings = CacheRuntimePolicy.FromPolicy("test", Policies.CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, Policies.CachePolicyFields.Duration);
        var keyGenerator = new Mock<ICacheKeyGenerator>();
        var expectedValue = "cached_value";

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        _innerManagerMock.Setup(x => x.TryGetAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CacheLookupResult<string>.Hit(expectedValue));

        _innerManagerMock.Setup(x => x.GetOrCreateAsync("test_key", It.IsAny<Func<CacheContext, CancellationToken, ValueTask<string>>>(), It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedValue);

        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        // Act
        var result = await _telemetryCacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult("new_value"),
            settings,
            keyGenerator.Object);

        // Assert
        result.Should().Be(expectedValue);

        _activitySourceMock.Verify(x => x.SetCacheHit(activity, true), Times.Once);
        _meterProviderMock.Verify(x => x.RecordCacheHit(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()), Times.Never);
        _meterProviderMock.Verify(x => x.RecordOperationDuration(methodName, It.IsAny<double>(), It.IsAny<Dictionary<string, object?>>()), Times.Once);
        _baggagePropagatorMock.Verify(x => x.InjectBaggage(activity), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_CacheMiss_MarksSpanAsMissWithoutCountingIt()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1, "test" };
        var settings = CacheRuntimePolicy.FromPolicy("test", Policies.CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, Policies.CachePolicyFields.Duration);
        var keyGenerator = new Mock<ICacheKeyGenerator>();
        var expectedValue = "new_value";

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        _innerManagerMock.Setup(x => x.TryGetAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CacheLookupResult<string>.Miss);

        _innerManagerMock.Setup(x => x.GetOrCreateAsync("test_key", It.IsAny<Func<CacheContext, CancellationToken, ValueTask<string>>>(), It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<CacheContext, CancellationToken, ValueTask<string>>, CacheEntryOptions, CancellationToken>(
                async (_, factory, _, token) => await factory(new CacheContext("key"), token));

        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        // Act
        var result = await _telemetryCacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult(expectedValue),
            settings,
            keyGenerator.Object);

        // Assert
        result.Should().Be(expectedValue);

        _activitySourceMock.Verify(x => x.SetCacheHit(activity, false), Times.Once);
        _meterProviderMock.Verify(x => x.RecordCacheMiss(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()), Times.Never);
        _meterProviderMock.Verify(x => x.RecordOperationDuration(methodName, It.IsAny<double>(), It.IsAny<Dictionary<string, object?>>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_Exception_MarksSpanWithoutCountingError()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1, "test" };
        var settings = CacheRuntimePolicy.FromPolicy("test", Policies.CachePolicy.Empty, Policies.CachePolicyFields.None);
        var keyGenerator = new Mock<ICacheKeyGenerator>();
        var exception = new InvalidOperationException("Test error");

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        _innerManagerMock.Setup(x => x.GetOrCreateAsync("test_key", It.IsAny<Func<CacheContext, CancellationToken, ValueTask<string>>>(), It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _telemetryCacheManager.GetOrCreateAsync(
                methodName,
                args,
                () => Task.FromResult("value"),
                settings,
                keyGenerator.Object));

        _activitySourceMock.Verify(x => x.SetCacheError(activity, exception), Times.Once);
        // The cache manager reports load failures through ICacheMetricsProvider; counting here too would double it.
        _meterProviderMock.Verify(x => x.RecordCacheError(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateFastAsync_RefreshAheadHit_IsMarkedAsHit()
    {
        var policy = CacheRuntimePolicy.FromPolicy("test", Policies.CachePolicy.Empty, Policies.CachePolicyFields.None);
        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation("TestMethod", TracingConstants.Operations.Get)).Returns(activity);

        // Like a refresh-ahead hit: the factory runs in the background, outside the caller's execution context,
        // and the cached value returns at once.
        _innerManagerMock.Setup(x => x.GetOrCreateAsync("key", It.IsAny<Func<CacheContext, CancellationToken, ValueTask<string>>>(), It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Func<CacheContext, CancellationToken, ValueTask<string>> factory, CacheEntryOptions _, CancellationToken token) =>
            {
                Task refresh;
                using (ExecutionContext.SuppressFlow())
                {
                    refresh = Task.Run(() => factory(new CacheContext("key"), token).AsTask());
                }

                refresh.Wait();
                return ValueTask.FromResult("cached");
            });

        var result = await _telemetryCacheManager.GetOrCreateFastAsync("key", "TestMethod", () => Task.FromResult("fresh"), policy);

        result.Should().Be("cached");
        _activitySourceMock.Verify(x => x.SetCacheHit(activity, true), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateFastAsync_AsyncMiss_IsMarkedAsMiss()
    {
        var policy = CacheRuntimePolicy.FromPolicy("test", Policies.CachePolicy.Empty, Policies.CachePolicyFields.None);
        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation("TestMethod", TracingConstants.Operations.Get)).Returns(activity);

        // Like a remote store: the lookup completes asynchronously before the factory runs.
        _innerManagerMock.Setup(x => x.GetOrCreateAsync("key", It.IsAny<Func<CacheContext, CancellationToken, ValueTask<string>>>(), It.IsAny<CacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, Func<CacheContext, CancellationToken, ValueTask<string>> factory, CacheEntryOptions _, CancellationToken token) =>
            {
                await Task.Yield();
                return await factory(new CacheContext("key"), token);
            });

        var result = await _telemetryCacheManager.GetOrCreateFastAsync("key", "TestMethod", () => Task.FromResult("fresh"), policy);

        result.Should().Be("fresh");
        _activitySourceMock.Verify(x => x.SetCacheHit(activity, false), Times.Once);
    }

    [Fact]
    public async Task InvalidateByTagsAsync_RecordsMetrics()
    {
        // Arrange
        var tags = new[] { "tag1", "tag2" };
        var activity = new Activity("test");

        _activitySourceMock.Setup(x => x.StartCacheOperation("InvalidateByTags", TracingConstants.Operations.Delete))
            .Returns(activity);

        // Act
        await _telemetryCacheManager.InvalidateByTagsAsync(tags);

        // Assert
        _innerManagerMock.Verify(x => x.InvalidateTagsAsync(tags, It.IsAny<CancellationToken>()), Times.Once);
        _activitySourceMock.Verify(x => x.SetCacheTags(activity, tags), Times.Once);
        _meterProviderMock.Verify(x => x.RecordOperationDuration(
            "InvalidateByTags",
            It.IsAny<double>(),
            It.Is<Dictionary<string, object?>>(d => d.ContainsKey("operation") && d["operation"]!.ToString() == "invalidate_by_tags")),
            Times.Once);
    }

    [Fact]
    public async Task TryGetAsync_MarksSpanHitAndMissWithoutCountingThem()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1 };
        var settings = CacheRuntimePolicy.FromPolicy("test", Policies.CachePolicy.Empty, Policies.CachePolicyFields.None);
        var keyGenerator = new Mock<ICacheKeyGenerator>();

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        // Test cache hit
        _innerManagerMock.Setup(x => x.TryGetAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CacheLookupResult<string>.Hit("cached_value"));

        var hitResult = await _telemetryCacheManager.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object);

        hitResult.Should().Be("cached_value");
        _activitySourceMock.Verify(x => x.SetCacheHit(activity, true), Times.Once);
        _meterProviderMock.Verify(x => x.RecordCacheHit(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()), Times.Never);

        // Reset for cache miss test
        _activitySourceMock.Reset();
        _meterProviderMock.Reset();
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        // Test cache miss
        _innerManagerMock.Setup(x => x.TryGetAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CacheLookupResult<string>.Miss);

        var missResult = await _telemetryCacheManager.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object);

        missResult.Should().BeNull();
        _activitySourceMock.Verify(x => x.SetCacheHit(activity, false), Times.Once);
        _meterProviderMock.Verify(x => x.RecordCacheMiss(It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>()), Times.Never);
    }

    [Fact]
    public async Task GetOrCreateAsync_WithTags_SetsActivityTags()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1 };
        var tags = new[] { "user", "profile" };
        var metadata = new Dictionary<string, string?> { { "group", "users" } };
        var settings = CacheRuntimePolicy.FromPolicy(
            "test",
            Policies.CachePolicy.Empty with
            {
                Duration = TimeSpan.FromMinutes(10),
                Tags = new List<string>(tags),
                Version = 2
            },
            Policies.CachePolicyFields.Duration | Policies.CachePolicyFields.Tags | Policies.CachePolicyFields.Version,
            metadata);
        var keyGenerator = new Mock<ICacheKeyGenerator>();

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        _innerManagerMock.Setup(x => x.TryGetAsync<string>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CacheLookupResult<string>.Hit("value"));

        // Act
        await _telemetryCacheManager.GetOrCreateAsync(
            methodName,
            args,
            () => Task.FromResult("value"),
            settings,
            keyGenerator.Object);

        // Assert
        _activitySourceMock.Verify(x => x.SetCacheTags(activity, tags), Times.Once);
        activity.GetTagItem(TracingConstants.AttributeNames.CacheTtl).Should().Be(600.0);
        // Group is stored in metadata, so check if it was set correctly
        activity.GetTagItem(TracingConstants.AttributeNames.CacheVersion).Should().Be(2);
    }

    public void Dispose()
    {
        _tracerProvider?.Dispose();
    }
}