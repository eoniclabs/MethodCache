using System;

namespace MethodCache.Core.Configuration
{
    public class GlobalCacheSettings
    {
        public TimeSpan? DefaultDuration { get; set; }
        public Type? DefaultKeyGeneratorType { get; set; }
    }
}
