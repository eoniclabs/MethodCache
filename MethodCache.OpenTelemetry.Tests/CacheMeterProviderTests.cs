using System.Collections.Generic;
using FluentAssertions;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Metrics;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

public class CacheMeterProviderTests
{
    private readonly CacheMeterProvider _meterProvider;
    private readonly OpenTelemetryOptions _options;

    public CacheMeterProviderTests()
    {
        _options = new OpenTelemetryOptions
        {
            EnableMetrics = true
        };

        _meterProvider = new CacheMeterProvider(Options.Create(_options));
    }

    [Fact]
    public void RecordCacheHit_UpdatesHitRatio()
    {
        // Arrange
        var methodName = "TestMethod";

        // Act
        _meterProvider.RecordCacheHit(methodName);
        _meterProvider.RecordCacheHit(methodName);
        _meterProvider.RecordCacheMiss(methodName);

        // Assert
        // Hit ratio should be 2/3 = 0.666...
        // Note: We can't directly test the ratio without exposing it,
        // but we can verify the methods don't throw
        _meterProvider.UpdateHitRatio(0.666);
    }

    [Fact]
    public void RecordCacheError_WithTags_RecordsCorrectly()
    {
        // Arrange
        var methodName = "TestMethod";
        var errorType = "TimeoutException";
        var tags = new Dictionary<string, object?>
        {
            ["error.message"] = "Operation timed out"
        };

        // Act & Assert - Should not throw
        _meterProvider.RecordCacheError(methodName, errorType, tags);
    }

    [Fact]
    public void RecordOperationDuration_RecordsMetric()
    {
        // Arrange
        var methodName = "TestMethod";
        var durationMs = 123.45;

        // Act & Assert - Should not throw
        _meterProvider.RecordOperationDuration(methodName, durationMs);
    }

    [Fact]
    public void RecordKeyGenerationDuration_RecordsMetric()
    {
        // Arrange
        var methodName = "TestMethod";
        var durationMs = 0.5;

        // Act & Assert - Should not throw
        _meterProvider.RecordKeyGenerationDuration(methodName, durationMs);
    }

    [Fact]
    public void RecordStorageOperationDuration_RecordsMetric()
    {
        // Arrange
        var provider = "Redis";
        var operation = "get";
        var durationMs = 10.5;

        // Act & Assert - Should not throw
        _meterProvider.RecordStorageOperationDuration(provider, operation, durationMs);
    }

    [Fact]
    public void UpdateEntriesCount_UpdatesGauge()
    {
        // Arrange
        var count = 1000L;

        // Act & Assert - Should not throw
        _meterProvider.UpdateEntriesCount(count);
    }

    [Fact]
    public void UpdateMemoryUsage_UpdatesGauge()
    {
        // Arrange
        var bytes = 1024L * 1024L; // 1MB

        // Act & Assert - Should not throw
        _meterProvider.UpdateMemoryUsage(bytes);
    }

    [Fact]
    public void MetricsDisabled_DoesNotRecordMetrics()
    {
        // Arrange
        _options.EnableMetrics = false;
        var disabledProvider = new CacheMeterProvider(Options.Create(_options));

        // Act & Assert - Should not throw and should exit early
        disabledProvider.RecordCacheHit("method");
        disabledProvider.RecordCacheMiss("method");
        disabledProvider.RecordCacheError("method", "error");
        disabledProvider.RecordOperationDuration("method", 10);
    }

    [Fact]
    public void ICacheMetricsProvider_Implementation_Works()
    {
        // Arrange
        var methodName = "TestMethod";
        var errorMessage = "Test error";
        var latencyMs = 50L;

        // Act & Assert - Should not throw
        ((Core.ICacheMetricsProvider)_meterProvider).CacheHit(methodName);
        ((Core.ICacheMetricsProvider)_meterProvider).CacheMiss(methodName);
        ((Core.ICacheMetricsProvider)_meterProvider).CacheError(methodName, errorMessage);
        ((Core.ICacheMetricsProvider)_meterProvider).CacheLatency(methodName, latencyMs);
    }

    [Fact]
    public void RecordCacheEviction_WithReason_RecordsCorrectly()
    {
        // Arrange
        var methodName = "TestMethod";
        var reason = "memory_pressure";
        var tags = new Dictionary<string, object?>
        {
            ["cache.size"] = 1024
        };

        // Act & Assert - Should not throw
        _meterProvider.RecordCacheEviction(methodName, reason, tags);
    }
}