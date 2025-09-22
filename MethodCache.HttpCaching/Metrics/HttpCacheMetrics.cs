using System.Collections.Concurrent;
using System.Diagnostics;

namespace MethodCache.HttpCaching.Metrics;

/// <summary>
/// Collects and provides metrics for HTTP cache operations.
/// </summary>
public class HttpCacheMetrics : IHttpCacheMetrics
{
    private long _hitCount;
    private long _missCount;
    private long _staleServedCount;
    private long _validationRequestCount;
    private long _bypassCount;
    private long _errorCount;
    private long _totalResponseTimeMs;
    private long _totalOperations;

    private readonly ConcurrentDictionary<string, long> _statusCodeCounts = new();
    private readonly ConcurrentDictionary<string, long> _methodCounts = new();
    private readonly ConcurrentQueue<long> _recentResponseTimes = new();
    private readonly int _maxRecentSamples = 1000;

    /// <inheritdoc/>
    public long HitCount => _hitCount;

    /// <inheritdoc/>
    public long MissCount => _missCount;

    /// <inheritdoc/>
    public long StaleServedCount => _staleServedCount;

    /// <inheritdoc/>
    public long ValidationRequestCount => _validationRequestCount;

    /// <inheritdoc/>
    public long BypassCount => _bypassCount;

    /// <inheritdoc/>
    public long ErrorCount => _errorCount;

    /// <inheritdoc/>
    public long TotalRequests => _hitCount + _missCount + _bypassCount;

    /// <inheritdoc/>
    public double HitRate => TotalRequests > 0 ? (double)_hitCount / TotalRequests : 0;

    /// <inheritdoc/>
    public double AverageResponseTimeMs => _totalOperations > 0 ? (double)_totalResponseTimeMs / _totalOperations : 0;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, long> StatusCodeCounts => _statusCodeCounts;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, long> MethodCounts => _methodCounts;

    /// <summary>
    /// Records a cache hit.
    /// </summary>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    public void RecordHit(long responseTimeMs)
    {
        Interlocked.Increment(ref _hitCount);
        RecordResponseTime(responseTimeMs);
    }

    /// <summary>
    /// Records a cache miss.
    /// </summary>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    public void RecordMiss(long responseTimeMs)
    {
        Interlocked.Increment(ref _missCount);
        RecordResponseTime(responseTimeMs);
    }

    /// <summary>
    /// Records serving a stale response.
    /// </summary>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    public void RecordStaleServed(long responseTimeMs)
    {
        Interlocked.Increment(ref _staleServedCount);
        RecordResponseTime(responseTimeMs);
    }

    /// <summary>
    /// Records a validation request (304 response).
    /// </summary>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    public void RecordValidation(long responseTimeMs)
    {
        Interlocked.Increment(ref _validationRequestCount);
        RecordResponseTime(responseTimeMs);
    }

    /// <summary>
    /// Records a cache bypass.
    /// </summary>
    /// <param name="responseTimeMs">Response time in milliseconds.</param>
    public void RecordBypass(long responseTimeMs)
    {
        Interlocked.Increment(ref _bypassCount);
        RecordResponseTime(responseTimeMs);
    }

    /// <summary>
    /// Records an error.
    /// </summary>
    public void RecordError()
    {
        Interlocked.Increment(ref _errorCount);
    }

    /// <summary>
    /// Records a status code.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    public void RecordStatusCode(int statusCode)
    {
        var key = statusCode.ToString();
        _statusCodeCounts.AddOrUpdate(key, 1, (k, v) => v + 1);
    }

    /// <summary>
    /// Records an HTTP method.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    public void RecordMethod(string method)
    {
        _methodCounts.AddOrUpdate(method, 1, (k, v) => v + 1);
    }

    private void RecordResponseTime(long responseTimeMs)
    {
        Interlocked.Add(ref _totalResponseTimeMs, responseTimeMs);
        Interlocked.Increment(ref _totalOperations);

        _recentResponseTimes.Enqueue(responseTimeMs);

        // Keep queue size bounded
        while (_recentResponseTimes.Count > _maxRecentSamples)
        {
            _recentResponseTimes.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Gets percentile response times from recent samples.
    /// </summary>
    /// <param name="percentile">The percentile (0-100).</param>
    /// <returns>The response time at the given percentile.</returns>
    public long GetPercentileResponseTime(double percentile)
    {
        if (percentile < 0 || percentile > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile));

        var samples = _recentResponseTimes.ToArray();
        if (samples.Length == 0)
            return 0;

        Array.Sort(samples);
        var index = (int)Math.Ceiling(samples.Length * percentile / 100) - 1;
        return samples[Math.Max(0, Math.Min(index, samples.Length - 1))];
    }

    /// <summary>
    /// Resets all metrics.
    /// </summary>
    public void Reset()
    {
        _hitCount = 0;
        _missCount = 0;
        _staleServedCount = 0;
        _validationRequestCount = 0;
        _bypassCount = 0;
        _errorCount = 0;
        _totalResponseTimeMs = 0;
        _totalOperations = 0;
        _statusCodeCounts.Clear();
        _methodCounts.Clear();
        _recentResponseTimes.Clear();
    }

    /// <summary>
    /// Gets a snapshot of current metrics.
    /// </summary>
    /// <returns>A metrics snapshot.</returns>
    public HttpCacheMetricsSnapshot GetSnapshot()
    {
        return new HttpCacheMetricsSnapshot
        {
            Timestamp = DateTimeOffset.UtcNow,
            HitCount = HitCount,
            MissCount = MissCount,
            HitRate = HitRate,
            StaleServedCount = StaleServedCount,
            ValidationRequestCount = ValidationRequestCount,
            BypassCount = BypassCount,
            ErrorCount = ErrorCount,
            TotalRequests = TotalRequests,
            AverageResponseTimeMs = AverageResponseTimeMs,
            P50ResponseTimeMs = GetPercentileResponseTime(50),
            P90ResponseTimeMs = GetPercentileResponseTime(90),
            P99ResponseTimeMs = GetPercentileResponseTime(99),
            StatusCodeCounts = new Dictionary<string, long>(_statusCodeCounts),
            MethodCounts = new Dictionary<string, long>(_methodCounts)
        };
    }
}

/// <summary>
/// Interface for HTTP cache metrics collection.
/// </summary>
public interface IHttpCacheMetrics
{
    /// <summary>
    /// Gets the number of cache hits.
    /// </summary>
    long HitCount { get; }

    /// <summary>
    /// Gets the number of cache misses.
    /// </summary>
    long MissCount { get; }

    /// <summary>
    /// Gets the number of stale responses served.
    /// </summary>
    long StaleServedCount { get; }

    /// <summary>
    /// Gets the number of validation requests (304 responses).
    /// </summary>
    long ValidationRequestCount { get; }

    /// <summary>
    /// Gets the number of cache bypasses.
    /// </summary>
    long BypassCount { get; }

    /// <summary>
    /// Gets the number of errors.
    /// </summary>
    long ErrorCount { get; }

    /// <summary>
    /// Gets the total number of requests.
    /// </summary>
    long TotalRequests { get; }

    /// <summary>
    /// Gets the cache hit rate (0-1).
    /// </summary>
    double HitRate { get; }

    /// <summary>
    /// Gets the average response time in milliseconds.
    /// </summary>
    double AverageResponseTimeMs { get; }

    /// <summary>
    /// Gets counts by status code.
    /// </summary>
    IReadOnlyDictionary<string, long> StatusCodeCounts { get; }

    /// <summary>
    /// Gets counts by HTTP method.
    /// </summary>
    IReadOnlyDictionary<string, long> MethodCounts { get; }
}

/// <summary>
/// A snapshot of HTTP cache metrics at a point in time.
/// </summary>
public class HttpCacheMetricsSnapshot
{
    /// <summary>
    /// When this snapshot was taken.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Number of cache hits.
    /// </summary>
    public long HitCount { get; init; }

    /// <summary>
    /// Number of cache misses.
    /// </summary>
    public long MissCount { get; init; }

    /// <summary>
    /// Cache hit rate (0-1).
    /// </summary>
    public double HitRate { get; init; }

    /// <summary>
    /// Number of stale responses served.
    /// </summary>
    public long StaleServedCount { get; init; }

    /// <summary>
    /// Number of validation requests.
    /// </summary>
    public long ValidationRequestCount { get; init; }

    /// <summary>
    /// Number of cache bypasses.
    /// </summary>
    public long BypassCount { get; init; }

    /// <summary>
    /// Number of errors.
    /// </summary>
    public long ErrorCount { get; init; }

    /// <summary>
    /// Total number of requests.
    /// </summary>
    public long TotalRequests { get; init; }

    /// <summary>
    /// Average response time.
    /// </summary>
    public double AverageResponseTimeMs { get; init; }

    /// <summary>
    /// 50th percentile response time.
    /// </summary>
    public long P50ResponseTimeMs { get; init; }

    /// <summary>
    /// 90th percentile response time.
    /// </summary>
    public long P90ResponseTimeMs { get; init; }

    /// <summary>
    /// 99th percentile response time.
    /// </summary>
    public long P99ResponseTimeMs { get; init; }

    /// <summary>
    /// Counts by status code.
    /// </summary>
    public Dictionary<string, long> StatusCodeCounts { get; init; } = new();

    /// <summary>
    /// Counts by HTTP method.
    /// </summary>
    public Dictionary<string, long> MethodCounts { get; init; } = new();
}