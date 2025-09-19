using System.Collections.Generic;
using MethodCache.Core.Configuration;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;

namespace MethodCache.Core.Configuration.Fluent
{
    internal static class CacheEntryOptionsMapper
    {
        public static CacheMethodSettings ToCacheMethodSettings(CacheEntryOptions options)
        {
            var settings = new CacheMethodSettings
            {
                Duration = options.Duration,
                Tags = new List<string>(options.Tags),
                IsIdempotent = false,
                SlidingExpiration = options.SlidingExpiration ?? options.Duration,
                RefreshAhead = options.RefreshAhead,
                StampedeProtection = options.StampedeProtection,
                DistributedLock = options.DistributedLock,
                Metrics = options.Metrics,
                Version = options.Version,
                KeyGeneratorType = options.KeyGeneratorType,
                Condition = WrapPredicate(options.Predicate),
                OnHitAction = WrapCallbacks(options.OnHitCallbacks),
                OnMissAction = WrapCallbacks(options.OnMissCallbacks)
            };

            if (settings.SlidingExpiration == null && options.SlidingExpiration != null)
            {
                settings.SlidingExpiration = options.SlidingExpiration;
            }

            return settings;
        }

        private static Func<CacheExecutionContext, bool>? WrapPredicate(Func<CacheContext, bool>? predicate)
        {
            if (predicate == null)
            {
                return null;
            }

            return context => predicate(new CacheContext(context.MethodName, context.Services));
        }

        private static Action<CacheExecutionContext>? WrapCallbacks(IReadOnlyList<Action<CacheContext>> callbacks)
        {
            if (callbacks.Count == 0)
            {
                return null;
            }

            return context =>
            {
                var cacheContext = new CacheContext(context.MethodName, context.Services);
                foreach (var callback in callbacks)
                {
                    callback(cacheContext);
                }
            };
        }
    }
}
