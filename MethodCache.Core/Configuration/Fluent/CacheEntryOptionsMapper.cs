using System;
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
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var settings = new CacheMethodSettings
            {
                Duration = options.Duration,
                Tags = options.Tags != null ? new List<string>(options.Tags) : new List<string>(),
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

            return context =>
            {
                if (context == null)
                {
                    throw new ArgumentNullException(nameof(context));
                }
                return predicate(new CacheContext(context.MethodName, context.Services));
            };
        }

        private static Action<CacheExecutionContext>? WrapCallbacks(IReadOnlyList<Action<CacheContext>>? callbacks)
        {
            if (callbacks == null || callbacks.Count == 0)
            {
                return null;
            }

            return context =>
            {
                if (context == null)
                {
                    throw new ArgumentNullException(nameof(context));
                }

                var cacheContext = new CacheContext(context.MethodName, context.Services);
                foreach (var callback in callbacks)
                {
                    callback?.Invoke(cacheContext);
                }
            };
        }
    }
}
