using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.Runtime;

internal sealed class RuntimeOverrideConfigurationSource : IConfigurationSource
{
    private readonly ConcurrentDictionary<string, CacheMethodSettings> _overrides = new(StringComparer.Ordinal);

    internal event EventHandler<RuntimeOverridesChangedEventArgs>? OverridesChanged;

    public int Priority => int.MaxValue;

    public bool SupportsRuntimeUpdates => true;

    public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync() =>
        Task.FromResult<IEnumerable<MethodCacheConfigEntry>>(
            _overrides.Select(static kvp => CreateEntry(kvp.Key, kvp.Value)));

    internal void ApplyOverrides(IEnumerable<MethodCacheConfigEntry> entries)
    {
        var changed = new List<string>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.MethodKey))
            {
                continue;
            }

            _overrides[entry.MethodKey] = entry.Settings.Clone();
            changed.Add(entry.MethodKey);
        }

        if (changed.Count > 0)
        {
            OnOverridesChanged(RuntimeOverrideChangeKind.Upsert, changed);
        }
    }

    internal void Clear()
    {
        if (_overrides.IsEmpty)
        {
            return;
        }

        var keys = _overrides.Keys.ToArray();
        _overrides.Clear();
        OnOverridesChanged(RuntimeOverrideChangeKind.Cleared, keys);
    }

    internal IReadOnlyList<MethodCacheConfigEntry> GetOverrides()
    {
        return _overrides
            .Select(static kvp => CreateEntry(kvp.Key, kvp.Value))
            .ToList();
    }

    internal bool TryGetOverride(string methodKey, out CacheMethodSettings settings)
    {
        if (_overrides.TryGetValue(methodKey, out var existing))
        {
            settings = existing.Clone();
            return true;
        }

        settings = default !; 
        return false;
    }

    internal bool RemoveOverride(string methodKey)
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


