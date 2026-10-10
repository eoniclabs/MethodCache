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

/// <summary>
/// Adds tracing spans and operation durations around an <see cref="ICacheManager"/>.
/// </summary>
/// <remarks>
/// Hit, miss and load-failure counters are not recorded here: the inner cache manager and the generated
/// decorators report them through <see cref="MethodCache.Core.Infrastructure.ICacheMetricsProvider"/>, which
/// <see cref="CacheMeterProvider"/> implements. Recording them here as well would count every call twice.
/// </remarks>
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

            var tracked = new TrackedFactory<T>(factory);
            var result = await tracked.Run(f => _innerManager.GetOrCreateAsync(methodName, args, f, policy, keyGenerator));

            stopwatch.Stop();

            _activitySource.SetCacheHit(activity, !tracked.LoadedForCaller);
            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTagsFromPolicy(policy));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
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
            var requiresExistenceCheck =
                typeof(T).IsValueType &&
                Nullable.GetUnderlyingType(typeof(T)) is null &&
                EqualityComparer<T>.Default.Equals(result!, default!);
            var hit = requiresExistenceCheck
                ? _innerManager.TryGetFast<T>(cacheKey, out _)
                : result is not null;
            _activitySource.SetCacheHit(activity, hit);

            stopwatch.Stop();
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

    public bool TryGetFast<T>(string cacheKey, out T? value)
    {
        return _innerManager.TryGetFast(cacheKey, out value);
    }

    public async Task<T> GetOrCreateFastAsync<T>(string cacheKey, string methodName, Func<Task<T>> factory, CacheRuntimePolicy policy)
    {
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            _baggagePropagator.InjectBaggage(activity);
            _activitySource.SetCacheKey(activity, cacheKey);

            if (policy.Tags.Count > 0)
            {
                _activitySource.SetCacheTags(activity, policy.Tags.ToArray());
            }

            SetActivityTagsFromPolicy(activity, policy);

            var tracked = new TrackedFactory<T>(factory);
            var result = await tracked.Run(f => _innerManager.GetOrCreateFastAsync(cacheKey, methodName, f, policy));

            stopwatch.Stop();
            _activitySource.SetCacheHit(activity, !tracked.LoadedForCaller);

            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTagsFromPolicy(policy));

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordOperationDuration(methodName, stopwatch.Elapsed.TotalMilliseconds, CreateMetricTagsFromPolicy(policy));
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

    // ============= Helper methods =============

    /// <summary>
    /// Wraps a factory to tell whether the inner manager ran it to answer this call, which marks the call as a miss.
    /// </summary>
    /// <remarks>
    /// A refresh-ahead hit returns the cached value at once and runs the factory on the thread pool, so a factory
    /// call alone does not mean a miss. When the inner call completes synchronously, only a factory call made inline
    /// on the calling thread counts; when it completes later, the caller waited for the factory.
    /// </remarks>
    private sealed class TrackedFactory<T>
    {
        private readonly Func<Task<T>> _factory;
        private int _callerThreadId;
        private volatile bool _armed;
        private volatile bool _invoked;
        private volatile bool _invokedInline;
        private bool _completedSynchronously;

        public TrackedFactory(Func<Task<T>> factory) => _factory = factory;

        /// <summary>True when the factory produced the value this call returned.</summary>
        public bool LoadedForCaller => _completedSynchronously ? _invokedInline : _invoked;

        public Task<T> Run(Func<Func<Task<T>>, Task<T>> call)
        {
            _callerThreadId = Environment.CurrentManagedThreadId;
            _armed = true;
            try
            {
                var task = call(InvokeAsync);
                _completedSynchronously = task.IsCompleted;
                return task;
            }
            finally
            {
                _armed = false;
            }
        }

        private Task<T> InvokeAsync()
        {
            if (_armed && Environment.CurrentManagedThreadId == _callerThreadId)
            {
                _invokedInline = true;
            }

            _invoked = true;
            return _factory();
        }
    }

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
