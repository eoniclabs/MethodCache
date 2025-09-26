using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.OpenTelemetry.Configuration;

namespace MethodCache.OpenTelemetry.Metrics;

public interface ICacheMeterProvider
{
    void RecordCacheHit(string methodName, Dictionary<string, object?>? tags = null);
    void RecordCacheMiss(string methodName, Dictionary<string, object?>? tags = null);
    void RecordCacheError(string methodName, string errorType, Dictionary<string, object?>? tags = null);
    void RecordCacheEviction(string methodName, string reason, Dictionary<string, object?>? tags = null);
    void RecordOperationDuration(string methodName, double durationMs, Dictionary<string, object?>? tags = null);
    void RecordKeyGenerationDuration(string methodName, double durationMs);
    void RecordSerializationDuration(string methodName, double durationMs);
    void RecordStorageOperationDuration(string provider, string operation, double durationMs);
    void UpdateEntriesCount(long count);
    void UpdateMemoryUsage(long bytes);
    void UpdateHitRatio(double ratio);
}

public class CacheMeterProvider : ICacheMeterProvider, ICacheMetricsProvider, IDisposable
{
    private readonly Meter _meter;
    private readonly OpenTelemetryOptions _options;

    private readonly Counter<long> _hitsCounter;
    private readonly Counter<long> _missesCounter;
    private readonly Counter<long> _errorsCounter;
    private readonly Counter<long> _evictionsCounter;
    private readonly Histogram<double> _operationDuration;
    private readonly Histogram<double> _keyGenerationDuration;
    private readonly Histogram<double> _serializationDuration;
    private readonly Histogram<double> _storageOperationDuration;

    private long _currentEntries;
    private long _currentMemoryBytes;
    private double _currentHitRatio;

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
            _evictionsCounter = _meter.CreateCounter<long>(MetricInstruments.Names.EvictionsTotal);
            _operationDuration = _meter.CreateHistogram<double>(MetricInstruments.Names.OperationDuration);
            _keyGenerationDuration = _meter.CreateHistogram<double>(MetricInstruments.Names.KeyGenerationDuration);
            _serializationDuration = _meter.CreateHistogram<double>(MetricInstruments.Names.SerializationDuration);
            _storageOperationDuration = _meter.CreateHistogram<double>(MetricInstruments.Names.StorageOperationDuration);
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

        _evictionsCounter = _meter.CreateCounter<long>(
            MetricInstruments.Names.EvictionsTotal,
            MetricInstruments.Units.Count,
            MetricInstruments.Descriptions.EvictionsTotal);

        _operationDuration = _meter.CreateHistogram<double>(
            MetricInstruments.Names.OperationDuration,
            MetricInstruments.Units.Milliseconds,
            MetricInstruments.Descriptions.OperationDuration);

        _keyGenerationDuration = _meter.CreateHistogram<double>(
            MetricInstruments.Names.KeyGenerationDuration,
            MetricInstruments.Units.Milliseconds,
            MetricInstruments.Descriptions.KeyGenerationDuration);

        _serializationDuration = _meter.CreateHistogram<double>(
            MetricInstruments.Names.SerializationDuration,
            MetricInstruments.Units.Milliseconds,
            MetricInstruments.Descriptions.SerializationDuration);

        _storageOperationDuration = _meter.CreateHistogram<double>(
            MetricInstruments.Names.StorageOperationDuration,
            MetricInstruments.Units.Milliseconds,
            MetricInstruments.Descriptions.StorageOperationDuration);

        _meter.CreateObservableGauge(
            MetricInstruments.Names.EntriesCount,
            () => _currentEntries,
            MetricInstruments.Units.Count,
            MetricInstruments.Descriptions.EntriesCount);

        _meter.CreateObservableGauge(
            MetricInstruments.Names.MemoryBytes,
            () => _currentMemoryBytes,
            MetricInstruments.Units.Bytes,
            MetricInstruments.Descriptions.MemoryBytes);

        _meter.CreateObservableGauge(
            MetricInstruments.Names.HitRatio,
            () => _currentHitRatio,
            MetricInstruments.Units.Ratio,
            MetricInstruments.Descriptions.HitRatio);
    }

    public void RecordCacheHit(string methodName, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        _methodHits.AddOrUpdate(methodName, 1, (_, old) => old + 1);

        var tagList = CreateTagList(methodName, tags);
        _hitsCounter.Add(1, tagList.ToArray());

        UpdateHitRatioInternal();
    }

    public void RecordCacheMiss(string methodName, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        _methodMisses.AddOrUpdate(methodName, 1, (_, old) => old + 1);

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

    public void RecordCacheEviction(string methodName, string reason, Dictionary<string, object?>? tags = null)
    {
        if (!_options.EnableMetrics) return;

        var tagList = CreateTagList(methodName, tags);
        tagList.Add(new KeyValuePair<string, object?>("eviction.reason", reason));
        _evictionsCounter.Add(1, tagList.ToArray());
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

    public void RecordSerializationDuration(string methodName, double durationMs)
    {
        if (!_options.EnableMetrics) return;

        _serializationDuration.Record(durationMs,
            new KeyValuePair<string, object?>("method", methodName));
    }

    public void RecordStorageOperationDuration(string provider, string operation, double durationMs)
    {
        if (!_options.EnableMetrics) return;

        _storageOperationDuration.Record(durationMs,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("operation", operation));
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
        RecordCacheError(methodName, "generic", new Dictionary<string, object?>
        {
            ["error.message"] = errorMessage
        });
    }

    void ICacheMetricsProvider.CacheLatency(string methodName, long elapsedMilliseconds)
    {
        RecordOperationDuration(methodName, elapsedMilliseconds);
    }

    private void UpdateHitRatioInternal()
    {
        var totalHits = 0L;
        var totalMisses = 0L;

        foreach (var hits in _methodHits.Values)
            totalHits += hits;

        foreach (var misses in _methodMisses.Values)
            totalMisses += misses;

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