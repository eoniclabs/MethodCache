using MethodCache.Core;
using MethodCache.Core.Infrastructure;
using MethodCache.SourceGenerator.IntegrationTests.Models;

namespace MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

/// <summary>
/// Test implementation of ICacheMetricsProvider that tracks metrics for integration tests
/// </summary>
public class TestCacheMetricsProvider : ICacheMetricsProvider
{
    private readonly CacheMetrics _metrics = new();
    private readonly object _lock = new();

    public CacheMetrics Metrics
    {
        get
        {
            lock (_lock)
            {
                return new CacheMetrics
                {
                    HitCount = _metrics.HitCount,
                    MissCount = _metrics.MissCount,
                    ErrorCount = _metrics.ErrorCount,
                    TagInvalidations = new Dictionary<string, int>(_metrics.TagInvalidations)
                };
            }
        }
    }

    public void CacheHit(string methodName)
    {
        lock (_lock)
        {
            _metrics.HitCount++;
        }
    }

    public void CacheMiss(string methodName)
    {
        lock (_lock)
        {
            _metrics.MissCount++;
        }
    }

    public void CacheError(string methodName, string errorMessage)
    {
        lock (_lock)
        {
            _metrics.ErrorCount++;
        }
    }

    public void CacheLatency(string methodName, long elapsedMilliseconds)
    {
        // Not tracking latency in tests for now
    }

    public void RecordInvalidation(string[] tags)
    {
        lock (_lock)
        {
            foreach (var tag in tags)
            {
                _metrics.TagInvalidations.TryGetValue(tag, out var count);
                _metrics.TagInvalidations[tag] = count + 1;
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _metrics.Reset();
        }
    }

    // Wait for metrics to reach expected values (useful for async operations)
    public async Task<bool> WaitForMetricsAsync(
        int? expectedHits = null,
        int? expectedMisses = null,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            var current = Metrics;
            
            if ((expectedHits == null || current.HitCount >= expectedHits) &&
                (expectedMisses == null || current.MissCount >= expectedMisses))
            {
                return true;
            }

            await Task.Delay(10);
        }

        return false;
    }
}