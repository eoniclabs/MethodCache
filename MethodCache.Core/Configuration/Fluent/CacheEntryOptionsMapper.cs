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
                Condition = options.Predicate != null ? ctx => options.Predicate(new CacheContext(ctx.MethodName, ctx.Services)) : null
            };

            if (settings.SlidingExpiration == null && options.SlidingExpiration != null)
            {
                settings.SlidingExpiration = options.SlidingExpiration;
            }

            return settings;
        }

    }
}
