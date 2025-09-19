using System;

namespace MethodCache.Core.Configuration
{
    public interface IGroupConfiguration
    {
        IGroupConfiguration Duration(TimeSpan duration);
        IGroupConfiguration TagWith(string tag);
        IGroupConfiguration Version(int version);
        IGroupConfiguration KeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new();
        IGroupConfiguration When(Func<CacheExecutionContext, bool> condition);
        IGroupConfiguration OnHit(Action<CacheExecutionContext> onHitAction);
        IGroupConfiguration OnMiss(Action<CacheExecutionContext> onMissAction);
    }
}
