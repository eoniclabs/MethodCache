using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Runtime;
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
        CacheRuntimeDescriptor descriptor,
        ICacheKeyGenerator keyGenerator)
    {
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);

            var keyGenStopwatch = Stopwatch.StartNew();
            var cacheKey = keyGenerator.GenerateKey(methodName, args, descriptor);
            keyGenStopwatch.Stop();

            _meterProvider.RecordKeyGenerationDuration(methodName, keyGenStopwatch.Elapsed.TotalMilliseconds);
            _activitySource.SetCacheKey(activity, cacheKey);

            if (descriptor.Tags.Count > 0)
            {
                _activitySource.SetCacheTags(activity, descriptor.Tags.ToArray());
            }

            SetActivityTags(activity, descriptor);

            // Track whether the factory was invoked to determine hit/miss
            var factoryInvoked = false;
            async Task<T> InstrumentedFactory()
            {
                factoryInvoked = true;
                return await factory();
            }

            var result = await _innerManager.GetOrCreateAsync(methodName, args, InstrumentedFactory, descriptor, keyGenerator);

            stopwatch.Stop();

            // If factory was invoked, it was a cache miss
            if (factoryInvoked)
            {
                _activitySource.SetCacheHit(activity, false);
                _meterProvider.RecordCacheMiss(methodName, CreateMetricTags(descriptor));
            }
            else
            {
                _activitySource.SetCacheHit(activity, true);
                _meterProvider.RecordCacheHit(methodName, CreateMetricTags(descriptor));
            }

            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTags(descriptor));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError(methodName, ex.GetType().Name, CreateMetricTags(descriptor));
            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTags(descriptor));
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
        CacheRuntimeDescriptor descriptor,
        ICacheKeyGenerator keyGenerator)
    {
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);

            var cacheKey = keyGenerator.GenerateKey(methodName, args, descriptor);
            _activitySource.SetCacheKey(activity, cacheKey);
            SetActivityTags(activity, descriptor);

            var result = await _innerManager.TryGetAsync<T>(methodName, args, descriptor, keyGenerator);

            var hit = result != null && !EqualityComparer<T>.Default.Equals(result, default);
            _activitySource.SetCacheHit(activity, hit);

            stopwatch.Stop();

            if (hit)
            {
                _meterProvider.RecordCacheHit(methodName, CreateMetricTags(descriptor));
            }
            else
            {
                _meterProvider.RecordCacheMiss(methodName, CreateMetricTags(descriptor));
            }

            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTags(descriptor));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError(methodName, ex.GetType().Name, CreateMetricTags(descriptor));
            throw;
        }
    }

    private static void SetActivityTags(Activity? activity, CacheRuntimeDescriptor descriptor)
    {
        if (activity == null) return;

        if (descriptor.Duration.HasValue)
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheTtl, descriptor.Duration.Value.TotalSeconds);
        }

        if (descriptor.Metadata.TryGetValue("group", out var group))
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheGroup, group);
        }

        if (descriptor.Version.HasValue)
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheVersion, descriptor.Version.Value);
        }
    }

    private static Dictionary<string, object?>? CreateMetricTags(CacheRuntimeDescriptor descriptor)
    {
        var tags = new Dictionary<string, object?>();

        if (descriptor.Metadata.TryGetValue("group", out var group))
        {
            tags["group"] = group;
        }

        if (descriptor.Version.HasValue)
        {
            tags["version"] = descriptor.Version.Value;
        }

        if (descriptor.Tags.Count > 0)
        {
            tags["tags"] = string.Join(",", descriptor.Tags);
        }

        return tags.Count > 0 ? tags : null;
    }
}