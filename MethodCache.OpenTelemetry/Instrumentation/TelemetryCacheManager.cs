using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
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

    // ============= New CacheRuntimePolicy-based methods (primary implementation) =============

    public async Task<T> GetOrCreateAsync<T>(
        string methodName,
        object[] args,
        Func<Task<T>> factory,
        CacheRuntimePolicy policy,
        ICacheKeyGenerator keyGenerator)
    {
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);

            var keyGenStopwatch = Stopwatch.StartNew();
            var cacheKey = keyGenerator.GenerateKey(methodName, args, policy);
            keyGenStopwatch.Stop();

            _meterProvider.RecordKeyGenerationDuration(methodName, keyGenStopwatch.Elapsed.TotalMilliseconds);
            _activitySource.SetCacheKey(activity, cacheKey);

            if (policy.Tags.Count > 0)
            {
                _activitySource.SetCacheTags(activity, policy.Tags.ToArray());
            }

            SetActivityTagsFromPolicy(activity, policy);

            // Track whether the factory was invoked to determine hit/miss
            var factoryInvoked = false;
            async Task<T> InstrumentedFactory()
            {
                factoryInvoked = true;
                return await factory();
            }

            var result = await _innerManager.GetOrCreateAsync(methodName, args, InstrumentedFactory, policy, keyGenerator);

            stopwatch.Stop();

            // If factory was invoked, it was a cache miss
            if (factoryInvoked)
            {
                _activitySource.SetCacheHit(activity, false);
                _meterProvider.RecordCacheMiss(methodName, CreateMetricTagsFromPolicy(policy));
            }
            else
            {
                _activitySource.SetCacheHit(activity, true);
                _meterProvider.RecordCacheHit(methodName, CreateMetricTagsFromPolicy(policy));
            }

            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTagsFromPolicy(policy));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError(methodName, ex.GetType().Name, CreateMetricTagsFromPolicy(policy));
            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTagsFromPolicy(policy));
            throw;
        }
    }

    public async ValueTask<T?> TryGetAsync<T>(
        string methodName,
        object[] args,
        CacheRuntimePolicy policy,
        ICacheKeyGenerator keyGenerator)
    {
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);

            var cacheKey = keyGenerator.GenerateKey(methodName, args, policy);
            _activitySource.SetCacheKey(activity, cacheKey);
            SetActivityTagsFromPolicy(activity, policy);

            var result = await _innerManager.TryGetAsync<T>(methodName, args, policy, keyGenerator);

            var hit = result != null && !EqualityComparer<T>.Default.Equals(result, default);
            _activitySource.SetCacheHit(activity, hit);

            stopwatch.Stop();

            if (hit)
            {
                _meterProvider.RecordCacheHit(methodName, CreateMetricTagsFromPolicy(policy));
            }
            else
            {
                _meterProvider.RecordCacheMiss(methodName, CreateMetricTagsFromPolicy(policy));
            }

            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTagsFromPolicy(policy));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordCacheError(methodName, ex.GetType().Name, CreateMetricTagsFromPolicy(policy));
            throw;
        }
    }

    public ValueTask<T?> TryGetFastAsync<T>(string cacheKey)
    {
        // Fast path - minimal telemetry overhead for ultra-fast lookups
        return _innerManager.TryGetFastAsync<T>(cacheKey);
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

    // ============= Helper methods =============

    private static void SetActivityTagsFromPolicy(Activity? activity, CacheRuntimePolicy policy)
    {
        if (activity == null) return;

        if (policy.Duration.HasValue)
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheTtl, policy.Duration.Value.TotalSeconds);
        }

        if (policy.Metadata.TryGetValue("group", out var group))
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheGroup, group);
        }

        if (policy.Version.HasValue)
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheVersion, policy.Version.Value);
        }
    }

    private static Dictionary<string, object?>? CreateMetricTagsFromPolicy(CacheRuntimePolicy policy)
    {
        var tags = new Dictionary<string, object?>();

        if (policy.Metadata.TryGetValue("group", out var group))
        {
            tags["group"] = group;
        }

        if (policy.Version.HasValue)
        {
            tags["version"] = policy.Version.Value;
        }

        if (policy.Tags.Count > 0)
        {
            tags["tags"] = string.Join(",", policy.Tags);
        }

        return tags.Count > 0 ? tags : null;
    }
}