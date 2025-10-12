using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using FluentAssertions;
using MethodCache.Core;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using MethodCache.OpenTelemetry.Instrumentation;
using MethodCache.OpenTelemetry.Metrics;
using MethodCache.OpenTelemetry.Propagators;
using MethodCache.OpenTelemetry.Tracing;
using Microsoft.Extensions.Options;
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

    [Fact]
    public async Task GetOrCreateAsync_CacheHit_RecordsHitMetrics()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1, "test" };
        var settings = CacheRuntimePolicy.FromPolicy("test", Abstractions.Policies.CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, Abstractions.Policies.CachePolicyFields.Duration);
        var keyGenerator = new Mock<ICacheKeyGenerator>();
        var expectedValue = "cached_value";

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        _innerManagerMock.Setup(x => x.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object))
            .ReturnsAsync(expectedValue);

        _innerManagerMock.Setup(x => x.GetOrCreateAsync(
                methodName,
                args,
                It.IsAny<Func<Task<string>>>(),
                settings,
                keyGenerator.Object))
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
        _meterProviderMock.Verify(x => x.RecordCacheHit(methodName, It.IsAny<Dictionary<string, object?>>()), Times.Once);
        _meterProviderMock.Verify(x => x.RecordOperationDuration(methodName, It.IsAny<double>(), It.IsAny<Dictionary<string, object?>>()), Times.Once);
        _baggagePropagatorMock.Verify(x => x.InjectBaggage(activity), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_CacheMiss_RecordsMissMetrics()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1, "test" };
        var settings = CacheRuntimePolicy.FromPolicy("test", Abstractions.Policies.CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(5) }, Abstractions.Policies.CachePolicyFields.Duration);
        var keyGenerator = new Mock<ICacheKeyGenerator>();
        var expectedValue = "new_value";

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        _innerManagerMock.Setup(x => x.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object))
            .ReturnsAsync((string?)null);

        _innerManagerMock.Setup(x => x.GetOrCreateAsync(
                methodName,
                args,
                It.IsAny<Func<Task<string>>>(),
                settings,
                keyGenerator.Object))
            .Returns<string, object[], Func<Task<string>>, CacheRuntimePolicy, ICacheKeyGenerator>(
                async (_, _, factory, _, _) => await factory());

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
        _meterProviderMock.Verify(x => x.RecordCacheMiss(methodName, It.IsAny<Dictionary<string, object?>>()), Times.Once);
        _meterProviderMock.Verify(x => x.RecordOperationDuration(methodName, It.IsAny<double>(), It.IsAny<Dictionary<string, object?>>()), Times.Once);
    }

    [Fact]
    public async Task GetOrCreateAsync_Exception_RecordsError()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1, "test" };
        var settings = CacheRuntimePolicy.FromPolicy("test", Abstractions.Policies.CachePolicy.Empty, Abstractions.Policies.CachePolicyFields.None);
        var keyGenerator = new Mock<ICacheKeyGenerator>();
        var exception = new InvalidOperationException("Test error");

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        _innerManagerMock.Setup(x => x.GetOrCreateAsync(
                methodName,
                args,
                It.IsAny<Func<Task<string>>>(),
                settings,
                keyGenerator.Object))
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
        _meterProviderMock.Verify(x => x.RecordCacheError(methodName, "InvalidOperationException", It.IsAny<Dictionary<string, object?>>()), Times.Once);
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
        _innerManagerMock.Verify(x => x.InvalidateByTagsAsync(tags), Times.Once);
        _activitySourceMock.Verify(x => x.SetCacheTags(activity, tags), Times.Once);
        _meterProviderMock.Verify(x => x.RecordOperationDuration(
            "InvalidateByTags",
            It.IsAny<double>(),
            It.Is<Dictionary<string, object?>>(d => d.ContainsKey("operation") && d["operation"]!.ToString() == "invalidate_by_tags")),
            Times.Once);
    }

    [Fact]
    public async Task TryGetAsync_RecordsHitAndMissCorrectly()
    {
        // Arrange
        var methodName = "TestMethod";
        var args = new object[] { 1 };
        var settings = CacheRuntimePolicy.FromPolicy("test", Abstractions.Policies.CachePolicy.Empty, Abstractions.Policies.CachePolicyFields.None);
        var keyGenerator = new Mock<ICacheKeyGenerator>();

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        // Test cache hit
        _innerManagerMock.Setup(x => x.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object))
            .ReturnsAsync("cached_value");

        var hitResult = await _telemetryCacheManager.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object);

        hitResult.Should().Be("cached_value");
        _activitySourceMock.Verify(x => x.SetCacheHit(activity, true), Times.Once);
        _meterProviderMock.Verify(x => x.RecordCacheHit(methodName, It.IsAny<Dictionary<string, object?>>()), Times.Once);

        // Reset for cache miss test
        _activitySourceMock.Reset();
        _meterProviderMock.Reset();
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        // Test cache miss
        _innerManagerMock.Setup(x => x.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object))
            .ReturnsAsync((string?)null);

        var missResult = await _telemetryCacheManager.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object);

        missResult.Should().BeNull();
        _activitySourceMock.Verify(x => x.SetCacheHit(activity, false), Times.Once);
        _meterProviderMock.Verify(x => x.RecordCacheMiss(methodName, It.IsAny<Dictionary<string, object?>>()), Times.Once);
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
            Abstractions.Policies.CachePolicy.Empty with
            {
                Duration = TimeSpan.FromMinutes(10),
                Tags = new List<string>(tags),
                Version = 2
            },
            Abstractions.Policies.CachePolicyFields.Duration | Abstractions.Policies.CachePolicyFields.Tags | Abstractions.Policies.CachePolicyFields.Version,
            metadata);
        var keyGenerator = new Mock<ICacheKeyGenerator>();

        keyGenerator.Setup(x => x.GenerateKey(methodName, args, settings))
            .Returns("test_key");

        var activity = new Activity("test");
        _activitySourceMock.Setup(x => x.StartCacheOperation(methodName, TracingConstants.Operations.Get))
            .Returns(activity);

        _innerManagerMock.Setup(x => x.TryGetAsync<string>(methodName, args, settings, keyGenerator.Object))
            .ReturnsAsync("value");

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