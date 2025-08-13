using System;
using System.Collections.Generic;

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
    }
}
