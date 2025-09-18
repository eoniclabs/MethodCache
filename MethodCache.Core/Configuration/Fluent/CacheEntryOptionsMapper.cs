using System.Collections.Generic;
using MethodCache.Core.Configuration;
using MethodCache.Core.Options;

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
                IsIdempotent = true
            };

            return settings;
        }
    }
}
