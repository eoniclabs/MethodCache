using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration
{
    public class MethodCacheConfiguration : IMethodCacheConfiguration
    {
        private readonly Dictionary<string, CacheMethodSettings> _methodSettings = new Dictionary<string, CacheMethodSettings>();
        private readonly Dictionary<string, CacheMethodSettings> _groupSettings = new Dictionary<string, CacheMethodSettings>();
        private readonly Dictionary<string, string?> _methodGroupMap = new Dictionary<string, string?>();

        private ICacheKeyGenerator _defaultKeyGenerator = new DefaultCacheKeyGenerator();
        private TimeSpan? _defaultDuration;

        public IServiceConfiguration<T> ForService<T>()
        {
            return new ServiceConfiguration<T>(_methodSettings);
        }

        public void DefaultDuration(TimeSpan duration)
        {
            _defaultDuration = duration;
        }

        public void DefaultKeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new()
        {
            _defaultKeyGenerator = new TGenerator();
        }

        public IGroupConfiguration ForGroup(string groupName)
        {
            if (!_groupSettings.TryGetValue(groupName, out var settings))
            {
                settings = new CacheMethodSettings();
                _groupSettings[groupName] = settings;
            }
            return new GroupConfiguration(settings);
        }

        void IMethodCacheConfiguration.RegisterMethod<T>(Expression<Action<T>> method, string methodId, string? groupName)
        {
            SetMethodGroup(methodId, groupName);
        }

        public void SetMethodGroup(string methodId, string? groupName)
        {
            if (string.IsNullOrEmpty(methodId))
            {
                throw new ArgumentException("Method id must be provided.", nameof(methodId));
            }

            _methodGroupMap[methodId] = groupName;
        }

        public CacheMethodSettings GetMethodSettings(string methodId)
        {
            // Get the method-specific settings. If not found, start with an empty settings object.
            _methodSettings.TryGetValue(methodId, out var methodSpecificSettings);

            // Create a mutable copy to build the final effective settings
            var finalSettings = methodSpecificSettings?.Clone() ?? new CacheMethodSettings();

            // Apply group settings if available and not overridden by method-specific settings
            var groupName = GetGroupNameForMethod(methodId);
            if (groupName != null && _groupSettings.TryGetValue(groupName, out var groupSettings))
            {
                // Apply group settings only if method-specific settings don't override them
                if (!finalSettings.Duration.HasValue) finalSettings.Duration = groupSettings.Duration;
                if (!finalSettings.SlidingExpiration.HasValue) finalSettings.SlidingExpiration = groupSettings.SlidingExpiration;
                if (!finalSettings.RefreshAhead.HasValue) finalSettings.RefreshAhead = groupSettings.RefreshAhead;
                if (finalSettings.StampedeProtection == null && groupSettings.StampedeProtection != null)
                {
                    finalSettings.StampedeProtection = groupSettings.StampedeProtection;
                }
                if (finalSettings.DistributedLock == null && groupSettings.DistributedLock != null)
                {
                    finalSettings.DistributedLock = groupSettings.DistributedLock;
                }
                if (finalSettings.Metrics == null && groupSettings.Metrics != null)
                {
                    finalSettings.Metrics = groupSettings.Metrics;
                }

                // For tags: combine method and group tags (union behavior)
                // This ensures consistent behavior where group tags are always included
                // unless explicitly overridden by method-specific configuration
                var groupTagsToAdd = groupSettings.Tags.Where(groupTag => 
                    !finalSettings.Tags.Contains(groupTag)).ToArray();
                finalSettings.Tags.AddRange(groupTagsToAdd);
                
                // Note: Method tags are preserved, group tags are added unless already present

                if (!finalSettings.Version.HasValue) finalSettings.Version = groupSettings.Version;
                if (finalSettings.KeyGeneratorType == null) finalSettings.KeyGeneratorType = groupSettings.KeyGeneratorType;
                if (finalSettings.Condition == null) finalSettings.Condition = groupSettings.Condition;
                if (finalSettings.OnHitAction == null && groupSettings.OnHitAction != null)
                {
                    finalSettings.OnHitAction = groupSettings.OnHitAction;
                }
                if (finalSettings.OnMissAction == null && groupSettings.OnMissAction != null)
                {
                    finalSettings.OnMissAction = groupSettings.OnMissAction;
                }
                // Only apply group idempotent if method-specific didn't set it
                if (methodSpecificSettings == null || !methodSpecificSettings.IsIdempotent)
                {
                    finalSettings.IsIdempotent = groupSettings.IsIdempotent;
                }
            }

            // Apply global defaults if not overridden by method or group settings
            if (!finalSettings.Duration.HasValue) finalSettings.Duration = _defaultDuration;
            if (finalSettings.KeyGeneratorType == null) finalSettings.KeyGeneratorType = _defaultKeyGenerator.GetType();

            return finalSettings;
        }

        private string? GetGroupNameForMethod(string methodId)
        {
            _methodGroupMap.TryGetValue(methodId, out var groupName);
            return groupName;
        }

        public void SetMethodSettings(string methodId, CacheMethodSettings settings)
        {
            _methodSettings[methodId] = settings;
        }

        public CacheMethodSettings GetGroupSettings(string groupName)
        {
            return _groupSettings.TryGetValue(groupName, out var settings) ? settings : new CacheMethodSettings();
        }
        
        /// <summary>
        /// Adds method configuration by key
        /// </summary>
        public void AddMethod(string methodKey, CacheMethodSettings settings)
        {
            _methodSettings[methodKey] = settings;
        }
        
        /// <summary>
        /// Clears all method configurations
        /// </summary>
        public void ClearAllMethods()
        {
            _methodSettings.Clear();
        }

        internal IEnumerable<MethodCacheConfigEntry> ToConfigEntries()
        {
            foreach (var kvp in _methodSettings)
            {
                var key = kvp.Key;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var separator = key.LastIndexOf('.');
                if (separator <= 0)
                {
                    continue;
                }

                var serviceType = key.Substring(0, separator);
                var methodName = key.Substring(separator + 1);

                yield return new MethodCacheConfigEntry
                {
                    ServiceType = serviceType,
                    MethodName = methodName,
                    Settings = kvp.Value.Clone()
                };
            }
        }
    }
}
