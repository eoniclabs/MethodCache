
using System;

namespace MethodCache.Core
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CacheAttribute : Attribute
    {
        public string? GroupName { get; }
        public bool RequireIdempotent { get; set; }
        public string? Duration { get; set; }
        public string[]? Tags { get; set; }
        public int Version { get; set; } = -1;
        public Type? KeyGeneratorType { get; set; }

        public CacheAttribute(string? groupName = null)
        {
            GroupName = groupName;
            RequireIdempotent = false; // Default to false
        }
    }
}
