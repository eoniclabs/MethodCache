using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.Runtime;

internal sealed class RuntimePolicyOverrideStore
{
    private readonly ConcurrentDictionary<string, CacheMethodSettings> _overrides = new(StringComparer.Ordinal);

    internal event EventHandler<RuntimeOverridesChangedEventArgs>? OverridesChanged;

    public IReadOnlyList<MethodCacheConfigEntry> GetOverrides()
    {
        return _overrides
            .Select(static kvp => CreateEntry(kvp.Key, kvp.Value))
            .ToList();
    }

    public void ApplyOverrides(IEnumerable<MethodCacheConfigEntry> entries)
    {
        if (entries == null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        var changed = new List<string>();

        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.MethodKey))
            {
                continue;
            }

            _overrides[entry.MethodKey] = (entry.Settings ?? new CacheMethodSettings()).Clone();
            changed.Add(entry.MethodKey);
        }

        if (changed.Count > 0)
        {
            OnOverridesChanged(RuntimeOverrideChangeKind.Upsert, changed);
        }
    }

    public bool TryGetOverride(string methodKey, out CacheMethodSettings settings)
    {
        if (string.IsNullOrWhiteSpace(methodKey))
        {
            settings = default!;
            return false;
        }

        if (_overrides.TryGetValue(methodKey, out var existing))
        {
            settings = existing.Clone();
            return true;
        }

        settings = default!;
        return false;
    }

    public bool RemoveOverride(string methodKey)
    {
        if (string.IsNullOrWhiteSpace(methodKey))
        {
            return false;
        }

        var removed = _overrides.TryRemove(methodKey, out _);
        if (removed)
        {
            OnOverridesChanged(RuntimeOverrideChangeKind.Removed, new[] { methodKey });
        }

        return removed;
    }

    public void Clear()
    {
        if (_overrides.IsEmpty)
        {
            return;
        }

        var keys = _overrides.Keys.ToArray();
        _overrides.Clear();
        OnOverridesChanged(RuntimeOverrideChangeKind.Cleared, keys);
    }

    private void OnOverridesChanged(RuntimeOverrideChangeKind kind, IReadOnlyCollection<string> keys)
    {
        if (OverridesChanged == null)
        {
            return;
        }

        OverridesChanged.Invoke(this, new RuntimeOverridesChangedEventArgs(kind, keys));
    }

    private static MethodCacheConfigEntry CreateEntry(string methodKey, CacheMethodSettings settings)
    {
        var separator = methodKey.LastIndexOf('.');
        var serviceType = separator > 0 ? methodKey[..separator] : methodKey;
        var methodName = separator > 0 ? methodKey[(separator + 1)..] : string.Empty;

        return new MethodCacheConfigEntry
        {
            ServiceType = serviceType,
            MethodName = methodName,
            Settings = settings.Clone(),
            Priority = int.MaxValue
        };
    }
}
