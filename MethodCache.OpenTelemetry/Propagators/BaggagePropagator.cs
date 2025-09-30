using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
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
    private readonly AsyncLocal<Dictionary<string, string>?> _localBaggage = new();

    public BaggagePropagator(IOptions<OpenTelemetryOptions> options)
    {
        _options = options.Value;
    }

    public void InjectBaggage(Activity? activity)
    {
        if (activity == null || !_options.EnableBaggagePropagation)
            return;

        var baggage = _localBaggage.Value;
        if (baggage != null)
        {
            foreach (var kvp in baggage.Where(kvp => IsCacheBaggageKey(kvp.Key)))
            {
                activity.SetBaggage(kvp.Key, kvp.Value);
            }
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

        var baggage = _localBaggage.Value ?? new Dictionary<string, string>();
        var hasCacheBaggage = false;

        foreach (var baggageItem in activity.Baggage)
        {
            if (IsCacheBaggageKey(baggageItem.Key))
            {
                baggage[baggageItem.Key] = baggageItem.Value ?? "";
                hasCacheBaggage = true;
            }
        }

        if (hasCacheBaggage)
        {
            _localBaggage.Value = baggage;
        }
    }

    public void SetCacheCorrelationId(string correlationId)
    {
        if (!_options.EnableBaggagePropagation)
            return;

        var baggage = _localBaggage.Value ?? new Dictionary<string, string>();
        baggage[TracingConstants.BaggageKeys.CacheCorrelationId] = correlationId;
        _localBaggage.Value = baggage;
    }

    public void SetCacheInvalidationId(string invalidationId)
    {
        if (!_options.EnableBaggagePropagation)
            return;

        var baggage = _localBaggage.Value ?? new Dictionary<string, string>();
        baggage[TracingConstants.BaggageKeys.CacheInvalidationId] = invalidationId;
        _localBaggage.Value = baggage;
    }

    public void SetCacheUserId(string? userId)
    {
        if (!_options.EnableBaggagePropagation || string.IsNullOrEmpty(userId))
            return;

        var baggage = _localBaggage.Value ?? new Dictionary<string, string>();
        baggage[TracingConstants.BaggageKeys.CacheUserId] = userId;
        _localBaggage.Value = baggage;
    }

    public void SetCacheTenantId(string? tenantId)
    {
        if (!_options.EnableBaggagePropagation || string.IsNullOrEmpty(tenantId))
            return;

        var baggage = _localBaggage.Value ?? new Dictionary<string, string>();
        baggage[TracingConstants.BaggageKeys.CacheTenantId] = tenantId;
        _localBaggage.Value = baggage;
    }

    public string? GetCacheCorrelationId()
    {
        var baggage = _localBaggage.Value;
        return baggage != null && baggage.TryGetValue(TracingConstants.BaggageKeys.CacheCorrelationId, out var value) ? value : null;
    }

    public string? GetCacheInvalidationId()
    {
        var baggage = _localBaggage.Value;
        return baggage != null && baggage.TryGetValue(TracingConstants.BaggageKeys.CacheInvalidationId, out var value) ? value : null;
    }

    public string? GetCacheUserId()
    {
        var baggage = _localBaggage.Value;
        return baggage != null && baggage.TryGetValue(TracingConstants.BaggageKeys.CacheUserId, out var value) ? value : null;
    }

    public string? GetCacheTenantId()
    {
        var baggage = _localBaggage.Value;
        return baggage != null && baggage.TryGetValue(TracingConstants.BaggageKeys.CacheTenantId, out var value) ? value : null;
    }

    private static bool IsCacheBaggageKey(string key)
    {
        return key.StartsWith("cache.", StringComparison.OrdinalIgnoreCase);
    }
}