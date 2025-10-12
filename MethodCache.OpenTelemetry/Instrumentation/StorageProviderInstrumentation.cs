using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MethodCache.Core;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.OpenTelemetry.Metrics;
using MethodCache.OpenTelemetry.Tracing;

namespace MethodCache.OpenTelemetry.Instrumentation;

public interface IInstrumentedStorageProvider : IStorageProvider
{
    string ProviderName { get; }
}

public class InstrumentedStorageProvider : IInstrumentedStorageProvider
{
    private readonly IStorageProvider _innerProvider;
    private readonly ICacheActivitySource _activitySource;
    private readonly ICacheMeterProvider _meterProvider;
    public string ProviderName { get; }
    public string Name => _innerProvider.Name;

    public InstrumentedStorageProvider(
        IStorageProvider innerProvider,
        ICacheActivitySource activitySource,
        ICacheMeterProvider meterProvider,
        string providerName)
    {
        _innerProvider = innerProvider ?? throw new ArgumentNullException(nameof(innerProvider));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _meterProvider = meterProvider ?? throw new ArgumentNullException(nameof(meterProvider));
        ProviderName = providerName ?? "Unknown";
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity($"storage.{ProviderName}.get");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            activity?.SetTag(TracingConstants.AttributeNames.CacheProvider, ProviderName);
            _activitySource.SetCacheKey(activity, key);

            var result = await _innerProvider.GetAsync<T>(key, cancellationToken);

            stopwatch.Stop();
            _meterProvider.RecordStorageOperationDuration(ProviderName, "get", stopwatch.Elapsed.TotalMilliseconds);

            var hit = result != null;
            activity?.SetTag(TracingConstants.AttributeNames.CacheHit, hit);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordStorageOperationDuration(ProviderName, "get", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity($"storage.{ProviderName}.set");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            activity?.SetTag(TracingConstants.AttributeNames.CacheProvider, ProviderName);
            _activitySource.SetCacheKey(activity, key);
            activity?.SetTag(TracingConstants.AttributeNames.CacheTtl, expiration.TotalSeconds);

            await _innerProvider.SetAsync(key, value, expiration, cancellationToken);

            stopwatch.Stop();
            _meterProvider.RecordStorageOperationDuration(ProviderName, "set", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordStorageOperationDuration(ProviderName, "set", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity($"storage.{ProviderName}.set");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            activity?.SetTag(TracingConstants.AttributeNames.CacheProvider, ProviderName);
            _activitySource.SetCacheKey(activity, key);
            activity?.SetTag(TracingConstants.AttributeNames.CacheTtl, expiration.TotalSeconds);

            var tagArray = tags as string[] ?? tags.ToArray();
            if (tagArray.Length > 0)
            {
                _activitySource.SetCacheTags(activity, tagArray);
            }

            await _innerProvider.SetAsync(key, value, expiration, tags, cancellationToken);

            stopwatch.Stop();
            _meterProvider.RecordStorageOperationDuration(ProviderName, "set", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordStorageOperationDuration(ProviderName, "set", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity($"storage.{ProviderName}.delete");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            activity?.SetTag(TracingConstants.AttributeNames.CacheProvider, ProviderName);
            _activitySource.SetCacheKey(activity, key);

            await _innerProvider.RemoveAsync(key, cancellationToken);

            stopwatch.Stop();
            _meterProvider.RecordStorageOperationDuration(ProviderName, "delete", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordStorageOperationDuration(ProviderName, "delete", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity($"storage.{ProviderName}.remove_by_tag");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            activity?.SetTag(TracingConstants.AttributeNames.CacheProvider, ProviderName);
            activity?.SetTag("storage.tag", tag);

            await _innerProvider.RemoveByTagAsync(tag, cancellationToken);

            stopwatch.Stop();
            _meterProvider.RecordStorageOperationDuration(ProviderName, "remove_by_tag", stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordStorageOperationDuration(ProviderName, "remove_by_tag", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity($"storage.{ProviderName}.exists");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            activity?.SetTag(TracingConstants.AttributeNames.CacheProvider, ProviderName);
            _activitySource.SetCacheKey(activity, key);

            var result = await _innerProvider.ExistsAsync(key, cancellationToken);

            stopwatch.Stop();
            _meterProvider.RecordStorageOperationDuration(ProviderName, "exists", stopwatch.Elapsed.TotalMilliseconds);

            activity?.SetTag("storage.exists", result);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordStorageOperationDuration(ProviderName, "exists", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity($"storage.{ProviderName}.health_check");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            activity?.SetTag(TracingConstants.AttributeNames.CacheProvider, ProviderName);

            var result = await _innerProvider.GetHealthAsync(cancellationToken);

            stopwatch.Stop();
            _meterProvider.RecordStorageOperationDuration(ProviderName, "health_check", stopwatch.Elapsed.TotalMilliseconds);

            activity?.SetTag("health.status", result.ToString());

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _activitySource.SetCacheError(activity, ex);
            _meterProvider.RecordStorageOperationDuration(ProviderName, "health_check", stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public ValueTask<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        return _innerProvider.GetStatsAsync(cancellationToken);
    }
}