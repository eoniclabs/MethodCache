using System;
using System.Linq.Expressions;
using MethodCache.Core.Configuration;

namespace MethodCache.Core
{
    public interface IServiceConfiguration<T>
    {
        IMethodConfiguration Method(Expression<Action<T>> method);
    }

    public interface IMethodCacheConfiguration
    {
        void RegisterMethod<T>(Expression<Action<T>> method, string methodId, string? groupName);

        IServiceConfiguration<T> ForService<T>();

        void DefaultDuration(TimeSpan duration);
        void DefaultKeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new();
        IGroupConfiguration ForGroup(string groupName);
        void AddMethod(string methodKey, CacheMethodSettings settings);
        void SetMethodGroup(string methodKey, string? groupName);
    }
}
