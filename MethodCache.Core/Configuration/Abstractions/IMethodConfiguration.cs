using System;
using MethodCache.Core;

namespace MethodCache.Core.Configuration
{
    public interface IMethodConfiguration
    {
        IMethodConfiguration Duration(TimeSpan duration);
        IMethodConfiguration Duration(Func<CacheExecutionContext, TimeSpan> durationFactory);
        IMethodConfiguration TagWith(string tag);
        IMethodConfiguration TagWith(Func<CacheExecutionContext, string> tagFactory);
        IMethodConfiguration Version(int version);
        IMethodConfiguration KeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new();
        IMethodConfiguration When(Func<CacheExecutionContext, bool> condition);
        IMethodConfiguration OnHit(Action<CacheExecutionContext> onHitAction);
        IMethodConfiguration OnMiss(Action<CacheExecutionContext> onMissAction);
    }
}
