using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Core.Metrics;
using MethodCache.Core.Options;

namespace MethodCache.Core.Configuration
{
    public class CacheMethodSettings
    {
        public TimeSpan? Duration { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public int? Version { get; set; }
        public Type? KeyGeneratorType { get; set; }
        public Func<CacheExecutionContext, bool>? Condition { get; set; }
        public Action<CacheExecutionContext>? OnHitAction { get; set; }
        public Action<CacheExecutionContext>? OnMissAction { get; set; }
        public bool IsIdempotent { get; set; }
        public TimeSpan? SlidingExpiration { get; set; }
        public TimeSpan? RefreshAhead { get; set; }
        public StampedeProtectionOptions? StampedeProtection { get; set; }
        public DistributedLockOptions? DistributedLock { get; set; }
        public ICacheMetrics? Metrics { get; set; }
        public Dictionary<string, object?> Metadata { get; set; } = new Dictionary<string, object?>();

        public CacheMethodSettings Clone()
        {
            return new CacheMethodSettings
            {
                Duration = Duration,
                Tags = new List<string>(Tags),
                Version = Version,
                KeyGeneratorType = KeyGeneratorType,
                Condition = Condition,
                OnHitAction = OnHitAction,
                OnMissAction = OnMissAction,
                IsIdempotent = IsIdempotent,
                SlidingExpiration = SlidingExpiration,
                RefreshAhead = RefreshAhead,
                StampedeProtection = StampedeProtection,
                DistributedLock = DistributedLock,
                Metrics = Metrics,
                Metadata = CloneMetadata(Metadata)
            };
        }

        private static Dictionary<string, object?> CloneMetadata(Dictionary<string, object?> metadata)
        {
            if (metadata.Count == 0)
            {
                return new Dictionary<string, object?>();
            }

            var clone = new Dictionary<string, object?>(metadata.Count, metadata.Comparer);
            foreach (var (key, value) in metadata)
            {
                clone[key] = CloneMetadataValue(value);
            }

            return clone;
        }

        private static object? CloneMetadataValue(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case Array array:
                    return array.Clone();
                case ICloneable cloneable:
                    return cloneable.Clone();
                default:
                    return value;
            }
        }
    }
}
