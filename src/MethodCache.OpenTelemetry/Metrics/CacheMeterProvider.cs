using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using MethodCache.Extensibility;
using Microsoft.Extensions.Options;

namespace MethodCache.OpenTelemetry;

internal interface ICacheMeterProvider
{
    void RecordCacheHit(string methodName, Dictionary<string, object?>? tags = null);
    void RecordCacheMiss(string methodName, Dictionary<string, object?>? tags = null);
    void RecordCacheError(string methodName, string errorType, Dictionary<string, object?>? tags = null);
    void RecordOperationDuration(string methodName, double durationMs, Dictionary<string, object?>? tags = null);
    void RecordKeyGenerationDuration(string methodName, double durationMs);
    void UpdateEntriesCount(long count);
    void UpdateMemoryUsage(long bytes);
    void UpdateHitRatio(double ratio);
}

internal class CacheMeterProvider : ICacheMeterProvider, ICacheMetricsProvider, IDisposable
{
    private readonly Meter _meter;
    private readonly OpenTelemetryOptions _options;

    private readonly Counter<long> _hitsCounter;
    private readonly Counter<long> _missesCounter;
    private readonly Counter<long> _errorsCounter;
    private readonly Histogram<double> _operationDuration;
    private readonly Histogram<double> _loadDuration;
    private readonly Histogram<double> _keyGenerationDuration;
    private readonly ObservableGauge<double>? _hitRatio;

    private ICacheAdministrator? _observedCache;
    private long _currentEntries;
    private long _currentMemoryBytes;
    private double _currentHitRatio;

    // Totals for the hit ratio gauge, counted only while a listener reads it, so a hit writes no shared field otherwise
    private long _totalHits;
    private long _totalMisses;

    public CacheMeterProvider(IOptions<OpenTelemetryOptions> options)
    {
        _options = options.Value;

        if (!_options.EnableMetrics)
        {
            _meter = new Meter(MetricInstruments.MeterName, MetricInstruments.MeterVersion);
            _hitsCounter = _meter.CreateCounter<long>(MetricInstruments.Names.HitsTotal);
            _missesCounter = _meter.CreateCounter<long>(MetricInstruments.Names.MissesTotal);
            _errorsCounter = _meter.CreateCounter<long>(MetricInstruments.Names.ErrorsTotal);
            _operationDuration = _meter.CreateHistogram<double>(MetricInstruments.Names.OperationDuration);
            _loadDuration = _meter.CreateHistogram<double>(MetricInstruments.Names.LoadDuration);
            _keyGenerationDuration = _meter.CreateHistogram<double>(MetricInstruments.Names.KeyGenerationDuration);
            return;
        }

        _meter = new Meter(MetricInstruments.MeterName, MetricInstruments.MeterVersion);

        _hitsCounter = _meter.CreateCounter<long>(
            MetricInstruments.Names.HitsTotal,
            MetricInstruments.Units.Count,
            MetricInstruments.Descriptions.HitsTotal);

        _missesCounter = _meter.CreateCounter<long>(
            MetricInstruments.Names.MissesTotal,
            MetricInstruments.Units.Count,
            MetricInstruments.Descriptions.MissesTotal);

        _errorsCounter = _meter.CreateCounter<long>(
            MetricInstruments.Names.ErrorsTotal,
            MetricInstruments.Units.Count,
            MetricInstruments.Descriptions.ErrorsTotal);

        _operationDuration = _meter.CreateHistogram<double>(
            MetricInstruments.Names.OperationDuration,
            MetricInstruments.Units.Milliseconds,
            MetricInstruments.Descriptions.OperationDuration);

        _loadDuration = _meter.CreateHistogram<double>(
            MetricInstruments.Names.LoadDuration,
            MetricInstruments.Units.Milliseconds,
            MetricInstruments.Descriptions.LoadDuration);

        _keyGenerationDuration = _meter.CreateHistogram<double>(
            MetricInstruments.Names.KeyGenerationDuration,
            MetricInstruments.Units.Milliseconds,
            MetricInstruments.Descriptions.KeyGenerationDuration);

        _meter.CreateObservableGauge(
            MetricInstruments.Names.EntriesCount,
            () => ReadObservedStats()?.Entries ?? Interlocked.Read(ref _currentEntries),
            MetricInstruments.Units.Count,
            MetricInstruments.Descriptions.EntriesCount);

        _meter.CreateObservableGauge(
            MetricInstruments.Names.MemoryBytes,
            () => ReadObservedStats()?.MemoryUsage ?? Interlocked.Read(ref _currentMemoryBytes),
            MetricInstruments.Units.Bytes,
            MetricInstruments.Descriptions.MemoryBytes);

        _hitRatio = _meter.CreateObservableGauge(
            MetricInstruments.Names.HitRatio,
            ReadHitRatio,
            MetricInstruments.Units.Ratio,
            MetricInstruments.Descriptions.HitRatio);
    }

    /// <summary>
    /// Reports the entry count and memory usage gauges from <paramref name="cache"/> instead of from
    /// <see cref="UpdateEntriesCount"/> and <see cref="UpdateMemoryUsage"/>.
    /// </summary>
    public void ObserveCache(ICacheAdministrator cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        Volatile.Write(ref _observedCache, cache);
    }

    public void RecordCacheHit(string methodName, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        if (_hitRatio!.Enabled)
        {
            Interlocked.Increment(ref _totalHits);
        }

        if (_hitsCounter.Enabled)
        {
            var tagList = CreateTagList(methodName, tags);
            _hitsCounter.Add(1, in tagList);
        }
    }

    public void RecordCacheMiss(string methodName, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        if (_hitRatio!.Enabled)
        {
            Interlocked.Increment(ref _totalMisses);
        }

        if (_missesCounter.Enabled)
        {
            var tagList = CreateTagList(methodName, tags);
            _missesCounter.Add(1, in tagList);
        }
    }

    public void RecordCacheError(string methodName, string errorType, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics || !_errorsCounter.Enabled) return;

        var tagList = CreateTagList(methodName, tags);
        tagList.Add("error.type", errorType);
        _errorsCounter.Add(1, in tagList);
    }

    public void RecordOperationDuration(string methodName, double durationMs, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics || !_operationDuration.Enabled) return;

        var tagList = CreateTagList(methodName, tags);
        _operationDuration.Record(durationMs, in tagList);
    }

    public void RecordKeyGenerationDuration(string methodName, double durationMs)
    {
        if (!_options.EnableMetrics) return;

        _keyGenerationDuration.Record(durationMs,
            new KeyValuePair<string, object?>("method", methodName));
    }

    public void UpdateEntriesCount(long count)
    {
        Interlocked.Exchange(ref _currentEntries, count);
    }

    public void UpdateMemoryUsage(long bytes)
    {
        Interlocked.Exchange(ref _currentMemoryBytes, bytes);
    }

    public void UpdateHitRatio(double ratio)
    {
        _currentHitRatio = ratio;
    }

    void ICacheMetricsProvider.CacheHit(string methodName)
    {
        RecordCacheHit(methodName);
    }

    void ICacheMetricsProvider.CacheMiss(string methodName)
    {
        RecordCacheMiss(methodName);
    }

    void ICacheMetricsProvider.CacheError(string methodName, string errorMessage)
    {
        // The message is left out on purpose: as a metric tag it would create a time series per distinct message.
        RecordCacheError(methodName, "load_failed");
    }

    void ICacheMetricsProvider.CacheLatency(string methodName, TimeSpan elapsed)
    {
        if (!_options.EnableMetrics) return;

        _loadDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("method", methodName));
    }

    private ICacheStats? ReadObservedStats()
    {
        var cache = Volatile.Read(ref _observedCache);
        if (cache == null)
        {
            return null;
        }

        // Gauge callbacks are synchronous, so a cache whose statistics are not immediately available is skipped
        // rather than blocked on.
        var pending = cache.GetStatsAsync();
        return pending.IsCompletedSuccessfully ? pending.Result : null;
    }

    /// <summary>
    /// The observed cache's own hit ratio, which counts every call. Without one, the ratio of the hits and misses
    /// counted since a listener started reading it, or the last value set.
    /// </summary>
    private double ReadHitRatio()
    {
        if (ReadObservedStats() is { } stats && stats.Hits + stats.Misses > 0)
        {
            return stats.HitRatio;
        }

        var totalHits = Interlocked.Read(ref _totalHits);
        var total = totalHits + Interlocked.Read(ref _totalMisses);
        return total > 0 ? (double)totalHits / total : _currentHitRatio;
    }

    private static TagList CreateTagList(string methodName, Dictionary<string, object?>? additionalTags)
    {
        var tags = new TagList
        {
            new("method", methodName)
        };

        if (additionalTags != null)
        {
            foreach (var tag in additionalTags)
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    public void Dispose()
    {
        _meter?.Dispose();
    }
}