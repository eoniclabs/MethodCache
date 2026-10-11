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

/// <remarks>
/// The baggage lives in an <see cref="AsyncLocal{T}"/> and is never changed in place: every change stores a new
/// dictionary, so a value set in a child task does not leak into its parent or its siblings. The user and tenant
/// ids identify people, so they are put on spans only with <see cref="OpenTelemetryOptions.ExportSensitiveData"/>.
/// </remarks>
internal class BaggagePropagator : IBaggagePropagator
{
    private readonly OpenTelemetryOptions _options;
    private readonly AsyncLocal<IReadOnlyDictionary<string, string>?> _localBaggage = new();

    public BaggagePropagator(IOptions<OpenTelemetryOptions> options)
    {
        _options = options.Value;
    }

    public void InjectBaggage(Activity? activity)
    {
        if (activity == null || !_options.EnableBaggagePropagation)
            return;

        var baggage = _localBaggage.Value;
        if (baggage == null)
            return;

        foreach (var kvp in baggage)
        {
            if (IsCacheBaggageKey(kvp.Key) && !string.IsNullOrEmpty(kvp.Value) && (_options.ExportSensitiveData || !IsSensitive(kvp.Key)))
            {
                activity.SetBaggage(kvp.Key, kvp.Value);
            }
        }
    }

    public void ExtractBaggage(Activity? activity)
    {
        if (activity == null || !_options.EnableBaggagePropagation)
            return;

        Dictionary<string, string>? updated = null;
        foreach (var baggageItem in activity.Baggage)
        {
            if (IsCacheBaggageKey(baggageItem.Key))
            {
                updated ??= Copy();
                updated[baggageItem.Key] = baggageItem.Value ?? "";
            }
        }

        if (updated != null)
        {
            _localBaggage.Value = updated;
        }
    }

    public void SetCacheCorrelationId(string correlationId) => Set(TracingConstants.BaggageKeys.CacheCorrelationId, correlationId);

    public void SetCacheInvalidationId(string invalidationId) => Set(TracingConstants.BaggageKeys.CacheInvalidationId, invalidationId);

    public void SetCacheUserId(string? userId) => Set(TracingConstants.BaggageKeys.CacheUserId, userId);

    public void SetCacheTenantId(string? tenantId) => Set(TracingConstants.BaggageKeys.CacheTenantId, tenantId);

    public string? GetCacheCorrelationId() => Get(TracingConstants.BaggageKeys.CacheCorrelationId);

    public string? GetCacheInvalidationId() => Get(TracingConstants.BaggageKeys.CacheInvalidationId);

    public string? GetCacheUserId() => Get(TracingConstants.BaggageKeys.CacheUserId);

    public string? GetCacheTenantId() => Get(TracingConstants.BaggageKeys.CacheTenantId);

    private void Set(string key, string? value)
    {
        if (!_options.EnableBaggagePropagation || string.IsNullOrEmpty(value))
            return;

        var updated = Copy();
        updated[key] = value;
        _localBaggage.Value = updated;
    }

    private string? Get(string key)
    {
        var baggage = _localBaggage.Value;
        return baggage != null && baggage.TryGetValue(key, out var value) ? value : null;
    }

    private Dictionary<string, string> Copy()
    {
        var current = _localBaggage.Value;
        return current == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(current, StringComparer.Ordinal);
    }

    private static bool IsSensitive(string key)
        => string.Equals(key, TracingConstants.BaggageKeys.CacheUserId, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(key, TracingConstants.BaggageKeys.CacheTenantId, StringComparison.OrdinalIgnoreCase);

    private static bool IsCacheBaggageKey(string key)
    {
        return key.StartsWith("cache.", StringComparison.OrdinalIgnoreCase);
    }
}
