using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Abstractions.Registry;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Configuration.Policies;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.Runtime
{
    internal sealed class RuntimeCacheConfigurator : IRuntimeCacheConfigurator
    {
        private readonly RuntimePolicyOverrideStore _store;
        private readonly IPolicyRegistry _policyRegistry;

        public RuntimeCacheConfigurator(RuntimePolicyOverrideStore store, IPolicyRegistry policyRegistry)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _policyRegistry = policyRegistry ?? throw new ArgumentNullException(nameof(policyRegistry));
        }

        public Task ApplyFluentAsync(Action<IFluentMethodCacheConfiguration> configure, CancellationToken cancellationToken = default)
        {
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            cancellationToken.ThrowIfCancellationRequested();

            var fluent = new FluentMethodCacheConfiguration();
            configure(fluent);

            var configuration = new MethodCacheConfiguration();
            fluent.Apply(configuration);

            var entries = new List<MethodCacheConfigEntry>();
            foreach (var entry in configuration.ToConfigEntries())
            {
                entry.Priority = int.MaxValue;
                entries.Add(entry);
            }

            _store.ApplyOverrides(entries);
            return Task.CompletedTask;
        }

        public Task ApplyOverridesAsync(IEnumerable<MethodCacheConfigEntry> overrides, CancellationToken cancellationToken = default)
        {
            if (overrides == null) throw new ArgumentNullException(nameof(overrides));

            cancellationToken.ThrowIfCancellationRequested();

            var normalized = new List<MethodCacheConfigEntry>();

            foreach (var entry in overrides)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.MethodName))
                {
                    continue;
                }

                normalized.Add(new MethodCacheConfigEntry
                {
                    ServiceType = NormalizeServiceType(entry.ServiceType),
                    MethodName = entry.MethodName,
                    Settings = (entry.Settings ?? new CacheMethodSettings()).Clone(),
                    Priority = int.MaxValue
                });
            }

            if (normalized.Count == 0)
            {
                return Task.CompletedTask;
            }

            _store.ApplyOverrides(normalized);
            return Task.CompletedTask;
        }

        public Task ClearOverridesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _store.Clear();
            return Task.CompletedTask;
        }

        public Task<bool> RemoveOverrideAsync(string serviceType, string methodName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serviceType))
            {
                throw new ArgumentException("Service type must be provided.", nameof(serviceType));
            }

            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException("Method name must be provided.", nameof(methodName));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var removed = _store.RemoveOverride(BuildMethodKey(serviceType, methodName));
            return Task.FromResult(removed);
        }

        public Task<IReadOnlyList<MethodCacheConfigEntry>> GetOverridesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(_store.GetOverrides());
        }

        public Task<IReadOnlyList<MethodCacheConfigEntry>> GetEffectiveConfigurationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var policies = _policyRegistry.GetAllPolicies();
            var entries = new List<MethodCacheConfigEntry>(policies.Count);

            foreach (var result in policies)
            {
                var (serviceType, methodName) = SplitMethodKey(result.MethodId);
                entries.Add(new MethodCacheConfigEntry
                {
                    ServiceType = serviceType,
                    MethodName = methodName,
                    Settings = CachePolicyConversion.ToCacheMethodSettings(result.Policy)
                });
            }

            entries.Sort(static (a, b) => string.CompareOrdinal(a.MethodKey, b.MethodKey));

            return Task.FromResult<IReadOnlyList<MethodCacheConfigEntry>>(entries);
        }

        private static string BuildMethodKey(string serviceType, string methodName) =>
            string.IsNullOrWhiteSpace(serviceType)
                ? methodName
                : $"{NormalizeServiceType(serviceType)}.{methodName}";

        private static (string ServiceType, string MethodName) SplitMethodKey(string methodKey)
        {
            if (string.IsNullOrWhiteSpace(methodKey))
            {
                return (string.Empty, string.Empty);
            }

            var separator = methodKey.LastIndexOf('.');
            if (separator <= 0)
            {
                return (methodKey, string.Empty);
            }

            var serviceType = methodKey[..separator];
            var methodName = methodKey[(separator + 1)..];
            return (serviceType, methodName);
        }

        private static string NormalizeServiceType(string serviceType) =>
            string.IsNullOrWhiteSpace(serviceType)
                ? string.Empty
                : serviceType.Replace('+', '.');
    }
}
