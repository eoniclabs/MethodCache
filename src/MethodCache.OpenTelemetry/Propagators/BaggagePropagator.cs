using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Options;

namespace MethodCache.OpenTelemetry;

/// <summary>
/// Carries cache correlation data (a correlation id, an invalidation id, a user id and a tenant id) along the async
/// flow and puts it into the baggage of MethodCache's spans. Resolve it from dependency injection after
/// <c>AddMethodCacheOpenTelemetry()</c>. Setting a null or empty value removes that value from the current async
/// flow, so a worker that handles one message after another doesn't label a message without a user with the last one.
/// </summary>
public interface IBaggagePropagator
{
    /// <summary>
    /// Copies the cache baggage onto <paramref name="activity"/>. The user and tenant ids are left out unless
    /// <see cref="OpenTelemetryOptions.ExportSensitiveData"/> is on.
    /// </summary>
    /// <param name="activity">The span to add the baggage to; nothing happens when it is null.</param>
    void InjectBaggage(Activity? activity);

    /// <summary>
    /// Reads the <c>cache.*</c> baggage of <paramref name="activity"/> into the current async flow.
    /// </summary>
    /// <param name="activity">The span to read from; nothing happens when it is null.</param>
    void ExtractBaggage(Activity? activity);

    /// <summary>
    /// Sets the correlation id for the current async flow.
    /// </summary>
    /// <param name="correlationId">The correlation id.</param>
    void SetCacheCorrelationId(string correlationId);

    /// <summary>
    /// Sets the id of the invalidation in progress for the current async flow.
    /// </summary>
    /// <param name="invalidationId">The invalidation id.</param>
    void SetCacheInvalidationId(string invalidationId);

    /// <summary>
    /// Sets the user id for the current async flow. It identifies a person, so it reaches spans only with
    /// <see cref="OpenTelemetryOptions.ExportSensitiveData"/>.
    /// </summary>
    /// <param name="userId">The user id; null or empty removes it.</param>
    void SetCacheUserId(string? userId);

    /// <summary>
    /// Sets the tenant id for the current async flow. It reaches spans only with
    /// <see cref="OpenTelemetryOptions.ExportSensitiveData"/>.
    /// </summary>
    /// <param name="tenantId">The tenant id; null or empty removes it.</param>
    void SetCacheTenantId(string? tenantId);

    /// <summary>
    /// Returns the correlation id of the current async flow, or null.
    /// </summary>
    /// <returns>The correlation id, or null when none is set.</returns>
    string? GetCacheCorrelationId();

    /// <summary>
    /// Returns the invalidation id of the current async flow, or null.
    /// </summary>
    /// <returns>The invalidation id, or null when none is set.</returns>
    string? GetCacheInvalidationId();

    /// <summary>
    /// Returns the user id of the current async flow, or null.
    /// </summary>
    /// <returns>The user id, or null when none is set.</returns>
    string? GetCacheUserId();

    /// <summary>
    /// Returns the tenant id of the current async flow, or null.
    /// </summary>
    /// <returns>The tenant id, or null when none is set.</returns>
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
        if (!_options.EnableBaggagePropagation)
            return;

        var updated = Copy();
        if (string.IsNullOrEmpty(value))
        {
            if (!updated.Remove(key))
                return;
        }
        else
        {
            updated[key] = value;
        }

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
