using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.OpenTelemetry.Configuration;

namespace MethodCache.OpenTelemetry.Metrics;

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

    private IMemoryCache? _observedCache;
    private long _currentEntries;
    private long _currentMemoryBytes;
    private double _currentHitRatio;

    // Aggregate counters for O(1) hit ratio calculation
    private long _totalHits;
    private long _totalMisses;

    private readonly ConcurrentDictionary<string, long> _methodHits = new();
    private readonly ConcurrentDictionary<string, long> _methodMisses = new();

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

        _meter.CreateObservableGauge(
            MetricInstruments.Names.HitRatio,
            () => _currentHitRatio,
            MetricInstruments.Units.Ratio,
            MetricInstruments.Descriptions.HitRatio);
    }

    /// <summary>
    /// Reports the entry count and memory usage gauges from <paramref name="cache"/> instead of from
    /// <see cref="UpdateEntriesCount"/> and <see cref="UpdateMemoryUsage"/>.
    /// </summary>
    public void ObserveCache(IMemoryCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        Volatile.Write(ref _observedCache, cache);
    }

    public void RecordCacheHit(string methodName, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        _methodHits.AddOrUpdate(methodName, 1, (_, old) => old + 1);
        Interlocked.Increment(ref _totalHits);

        var tagList = CreateTagList(methodName, tags);
        _hitsCounter.Add(1, tagList.ToArray());

        UpdateHitRatioInternal();
    }

    public void RecordCacheMiss(string methodName, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        _methodMisses.AddOrUpdate(methodName, 1, (_, old) => old + 1);
        Interlocked.Increment(ref _totalMisses);

        var tagList = CreateTagList(methodName, tags);
        _missesCounter.Add(1, tagList.ToArray());

        UpdateHitRatioInternal();
    }

    public void RecordCacheError(string methodName, string errorType, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        var tagList = CreateTagList(methodName, tags);
        tagList.Add(new KeyValuePair<string, object?>("error.type", errorType));
        _errorsCounter.Add(1, tagList.ToArray());
    }

    public void RecordOperationDuration(string methodName, double durationMs, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        var tagList = CreateTagList(methodName, tags);
        _operationDuration.Record(durationMs, tagList.ToArray());
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

    void ICacheMetricsProvider.CacheLatency(string methodName, long elapsedMilliseconds)
    {
        if (!_options.EnableMetrics) return;

        _loadDuration.Record(elapsedMilliseconds, new KeyValuePair<string, object?>("method", methodName));
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

    private void UpdateHitRatioInternal()
    {
        var totalHits = Interlocked.Read(ref _totalHits);
        var totalMisses = Interlocked.Read(ref _totalMisses);

        var total = totalHits + totalMisses;
        _currentHitRatio = total > 0 ? (double)totalHits / total : 0;
    }

    private List<KeyValuePair<string, object?>> CreateTagList(string methodName, Dictionary<string, object?>? additionalTags)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("method", methodName)
        };

        if (additionalTags != null)
        {
            foreach (var tag in additionalTags)
            {
                tags.Add(new KeyValuePair<string, object?>(tag.Key, tag.Value));
            }
        }

        return tags;
    }

    public void Dispose()
    {
        _meter?.Dispose();
    }
}