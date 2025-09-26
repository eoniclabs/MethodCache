using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.OpenTelemetry.Metrics;
using MethodCache.OpenTelemetry.Propagators;
using MethodCache.OpenTelemetry.Tracing;

namespace MethodCache.OpenTelemetry.Instrumentation;

public class TelemetryCacheManager : ICacheManager
{
    private readonly ICacheManager _innerManager;
    private readonly ICacheActivitySource _activitySource;
    private readonly ICacheMeterProvider _meterProvider;
    private readonly IBaggagePropagator _baggagePropagator;

    public TelemetryCacheManager(
        ICacheManager innerManager,
        ICacheActivitySource activitySource,
        ICacheMeterProvider meterProvider,
        IBaggagePropagator baggagePropagator)
    {
        _innerManager = innerManager ?? throw new ArgumentNullException(nameof(innerManager));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _meterProvider = meterProvider ?? throw new ArgumentNullException(nameof(meterProvider));
        _baggagePropagator = baggagePropagator ?? throw new ArgumentNullException(nameof(baggagePropagator));
    }

    public async Task<T> GetOrCreateAsync<T>(
        string methodName,
        object[] args,
        Func<Task<T>> factory,
        CacheMethodSettings settings,
        ICacheKeyGenerator keyGenerator,
        bool requireIdempotent)
    {
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);

            var keyGenStopwatch = Stopwatch.StartNew();
            var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
            keyGenStopwatch.Stop();

            _meterProvider.RecordKeyGenerationDuration(methodName, keyGenStopwatch.Elapsed.TotalMilliseconds);
            _activitySource.SetCacheKey(activity, cacheKey);

            if (settings.Tags.Count > 0)
            {
                _activitySource.SetCacheTags(activity, settings.Tags.ToArray());
            }

            SetActivityTags(activity, settings);

            var existingValue = await _innerManager.TryGetAsync<T>(methodName, args, settings, keyGenerator);
            if (existingValue != null && !EqualityComparer<T>.Default.Equals(existingValue, default))
            {
                stopwatch.Stop();
                _activitySource.SetCacheHit(activity, true);
                _meterProvider.RecordCacheHit(methodName, CreateMetricTags(settings));
                _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTags(settings));
                return existingValue;
            }

            _activitySource.SetCacheHit(activity, false);
            _meterProvider.RecordCacheMiss(methodName, CreateMetricTags(settings));

            var result = await _innerManager.GetOrCreateAsync(methodName, args, factory, settings, keyGenerator, requireIdempotent);

            stopwatch.Stop();
            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTags(settings));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError(methodName, ex.GetType().Name, CreateMetricTags(settings));
            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTags(settings));
            throw;
        }
    }

    public async Task InvalidateByTagsAsync(params string[] tags)
    {
        using var activity = _activitySource.StartCacheOperation("InvalidateByTags", TracingConstants.Operations.Delete);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);
            _activitySource.SetCacheTags(activity, tags);

            await _innerManager.InvalidateByTagsAsync(tags);

            stopwatch.Stop();
            _meterProvider.RecordOperationDuration("InvalidateByTags", stopwatch.Elapsed.TotalMilliseconds,
                new Dictionary<string, object?> { ["operation"] = "invalidate_by_tags" });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError("InvalidateByTags", ex.GetType().Name);
            throw;
        }
    }

    public async Task InvalidateByKeysAsync(params string[] keys)
    {
        using var activity = _activitySource.StartCacheOperation("InvalidateByKeys", TracingConstants.Operations.Delete);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);
            activity?.SetTag("cache.keys_count", keys.Length);

            await _innerManager.InvalidateByKeysAsync(keys);

            stopwatch.Stop();
            _meterProvider.RecordOperationDuration("InvalidateByKeys", stopwatch.Elapsed.TotalMilliseconds,
                new Dictionary<string, object?> { ["operation"] = "invalidate_by_keys", ["keys_count"] = keys.Length });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError("InvalidateByKeys", ex.GetType().Name);
            throw;
        }
    }

    public async Task InvalidateByTagPatternAsync(string pattern)
    {
        using var activity = _activitySource.StartCacheOperation("InvalidateByTagPattern", TracingConstants.Operations.Delete);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);
            activity?.SetTag("cache.pattern", pattern);

            await _innerManager.InvalidateByTagPatternAsync(pattern);

            stopwatch.Stop();
            _meterProvider.RecordOperationDuration("InvalidateByTagPattern", stopwatch.Elapsed.TotalMilliseconds,
                new Dictionary<string, object?> { ["operation"] = "invalidate_by_pattern" });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError("InvalidateByTagPattern", ex.GetType().Name);
            throw;
        }
    }

    public async ValueTask<T?> TryGetAsync<T>(
        string methodName,
        object[] args,
        CacheMethodSettings settings,
        ICacheKeyGenerator keyGenerator)
    {
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);

            var cacheKey = keyGenerator.GenerateKey(methodName, args, settings);
            _activitySource.SetCacheKey(activity, cacheKey);
            SetActivityTags(activity, settings);

            var result = await _innerManager.TryGetAsync<T>(methodName, args, settings, keyGenerator);

            var hit = result != null && !EqualityComparer<T>.Default.Equals(result, default);
            _activitySource.SetCacheHit(activity, hit);

            stopwatch.Stop();

            if (hit)
            {
                _meterProvider.RecordCacheHit(methodName, CreateMetricTags(settings));
            }
            else
            {
                _meterProvider.RecordCacheMiss(methodName, CreateMetricTags(settings));
            }

            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTags(settings));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError(methodName, ex.GetType().Name, CreateMetricTags(settings));
            throw;
        }
    }

    private static void SetActivityTags(Activity? activity, CacheMethodSettings settings)
    {
        if (activity == null) return;

        if (settings.Duration.HasValue)
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheTtl, settings.Duration.Value.TotalSeconds);
        }

        if (settings.Metadata.TryGetValue("group", out var group))
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheGroup, group?.ToString());
        }

        if (settings.Version.HasValue)
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheVersion, settings.Version.Value);
        }
    }

    private static Dictionary<string, object?>? CreateMetricTags(CacheMethodSettings settings)
    {
        var tags = new Dictionary<string, object?>();

        if (settings.Metadata.TryGetValue("group", out var group))
        {
            tags["group"] = group?.ToString();
        }

        if (settings.Version.HasValue)
        {
            tags["version"] = settings.Version.Value;
        }

        if (settings.Tags.Count > 0)
        {
            tags["tags"] = string.Join(",", settings.Tags);
        }

        return tags.Count > 0 ? tags : null;
    }
}