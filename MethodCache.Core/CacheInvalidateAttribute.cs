using System;

namespace MethodCache.Core
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class CacheInvalidateAttribute : Attribute
    {
        public string[]? Tags { get; set; }
    }
}
