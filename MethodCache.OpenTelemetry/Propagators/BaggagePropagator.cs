using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Options;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Tracing;

namespace MethodCache.OpenTelemetry.Propagators;

public interface IBaggagePropagator
{
    void InjectBaggage(Activity? activity);
    void ExtractBaggage(Activity? activity);
    void SetCacheCorrelationId(string correlationId);
    void SetCacheInvalidationId(string invalidationId);
    void SetCacheUserId(string? userId);
    void SetCacheTenantId(string? tenantId);
    string? GetCacheCorrelationId();
    string? GetCacheInvalidationId();
    string? GetCacheUserId();
    string? GetCacheTenantId();
}

public class BaggagePropagator : IBaggagePropagator
{
    private readonly OpenTelemetryOptions _options;
    private readonly Dictionary<string, string> _localBaggage = new();

    public BaggagePropagator(IOptions<OpenTelemetryOptions> options)
    {
        _options = options.Value;
    }

    public void InjectBaggage(Activity? activity)
    {
        if (activity == null || !_options.EnableBaggagePropagation)
            return;

        foreach (var kvp in _localBaggage.Where(kvp => IsCacheBaggageKey(kvp.Key)))
        {
            activity.SetBaggage(kvp.Key, kvp.Value);
        }

        var correlationId = GetCacheCorrelationId();
        if (!string.IsNullOrEmpty(correlationId))
        {
            activity.SetBaggage(TracingConstants.BaggageKeys.CacheCorrelationId, correlationId);
        }

        var invalidationId = GetCacheInvalidationId();
        if (!string.IsNullOrEmpty(invalidationId))
        {
            activity.SetBaggage(TracingConstants.BaggageKeys.CacheInvalidationId, invalidationId);
        }

        var userId = GetCacheUserId();
        if (!string.IsNullOrEmpty(userId) && _options.ExportSensitiveData)
        {
            activity.SetBaggage(TracingConstants.BaggageKeys.CacheUserId, userId);
        }

        var tenantId = GetCacheTenantId();
        if (!string.IsNullOrEmpty(tenantId))
        {
            activity.SetBaggage(TracingConstants.BaggageKeys.CacheTenantId, tenantId);
        }
    }

    public void ExtractBaggage(Activity? activity)
    {
        if (activity == null || !_options.EnableBaggagePropagation)
            return;

        foreach (var baggageItem in activity.Baggage)
        {
            if (IsCacheBaggageKey(baggageItem.Key))
            {
                _localBaggage[baggageItem.Key] = baggageItem.Value ?? "";
            }
        }
    }

    public void SetCacheCorrelationId(string correlationId)
    {
        if (!_options.EnableBaggagePropagation)
            return;

        _localBaggage[TracingConstants.BaggageKeys.CacheCorrelationId] = correlationId;
    }

    public void SetCacheInvalidationId(string invalidationId)
    {
        if (!_options.EnableBaggagePropagation)
            return;

        _localBaggage[TracingConstants.BaggageKeys.CacheInvalidationId] = invalidationId;
    }

    public void SetCacheUserId(string? userId)
    {
        if (!_options.EnableBaggagePropagation || string.IsNullOrEmpty(userId))
            return;

        _localBaggage[TracingConstants.BaggageKeys.CacheUserId] = userId;
    }

    public void SetCacheTenantId(string? tenantId)
    {
        if (!_options.EnableBaggagePropagation || string.IsNullOrEmpty(tenantId))
            return;

        _localBaggage[TracingConstants.BaggageKeys.CacheTenantId] = tenantId;
    }

    public string? GetCacheCorrelationId()
    {
        return _localBaggage.TryGetValue(TracingConstants.BaggageKeys.CacheCorrelationId, out var value) ? value : null;
    }

    public string? GetCacheInvalidationId()
    {
        return _localBaggage.TryGetValue(TracingConstants.BaggageKeys.CacheInvalidationId, out var value) ? value : null;
    }

    public string? GetCacheUserId()
    {
        return _localBaggage.TryGetValue(TracingConstants.BaggageKeys.CacheUserId, out var value) ? value : null;
    }

    public string? GetCacheTenantId()
    {
        return _localBaggage.TryGetValue(TracingConstants.BaggageKeys.CacheTenantId, out var value) ? value : null;
    }

    private static bool IsCacheBaggageKey(string key)
    {
        return key.StartsWith("cache.", StringComparison.OrdinalIgnoreCase);
    }
}