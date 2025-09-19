using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MethodCache.Core.Configuration;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.Runtime
{
    internal sealed class RuntimeOverrideConfigurationSource : IConfigurationSource
    {
        private readonly ConcurrentDictionary<string, CacheMethodSettings> _overrides = new(StringComparer.Ordinal);

        public int Priority => int.MaxValue;

        public bool SupportsRuntimeUpdates => true;

        public Task<IEnumerable<MethodCacheConfigEntry>> LoadAsync() =>
            Task.FromResult<IEnumerable<MethodCacheConfigEntry>>(
                _overrides.Select(static kvp => CreateEntry(kvp.Key, kvp.Value)));

        internal void ApplyOverrides(IEnumerable<MethodCacheConfigEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.MethodKey))
                {
                    continue;
                }

                _overrides[entry.MethodKey] = entry.Settings.Clone();
            }
        }

        internal void Clear()
        {
            _overrides.Clear();
        }

        internal IReadOnlyList<MethodCacheConfigEntry> GetOverrides()
        {
            return _overrides
                .Select(static kvp => CreateEntry(kvp.Key, kvp.Value))
                .ToList();
        }

        internal bool RemoveOverride(string methodKey)
        {
            if (string.IsNullOrWhiteSpace(methodKey))
            {
                return false;
            }

            return _overrides.TryRemove(methodKey, out _);
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
}
