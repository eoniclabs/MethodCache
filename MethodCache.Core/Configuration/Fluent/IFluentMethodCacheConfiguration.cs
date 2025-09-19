using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using MethodCache.Core.Options;
using MethodCache.Core.Runtime;

namespace MethodCache.Core.Configuration.Fluent
{
    public interface IFluentMethodCacheConfiguration
    {
        IFluentMethodCacheConfiguration DefaultPolicy(Action<CacheEntryOptions.Builder> configure);

        IFluentServiceConfiguration<TService> ForService<TService>();

        IFluentGroupConfiguration ForGroup(string name);
    }

    public interface IFluentServiceConfiguration<TService>
    {
        IFluentMethodConfiguration Method(Expression<Action<TService>> method);

        IFluentMethodConfiguration Method<TResult>(Expression<Func<TService, TResult>> method);

        IFluentMethodConfiguration Method(Expression<Func<TService, Task>> method);

        IFluentMethodConfiguration Method<TResult>(Expression<Func<TService, Task<TResult>>> method);
    }

    public interface IFluentMethodConfiguration
    {
        IFluentMethodConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
        IFluentMethodConfiguration WithGroup(string groupName);
        IFluentMethodConfiguration RequireIdempotent(bool enabled = true);
        IFluentMethodConfiguration WithVersion(int version);
        IFluentMethodConfiguration WithKeyGenerator<TGenerator>() where TGenerator : ICacheKeyGenerator, new();
        IFluentMethodConfiguration When(Func<CacheContext, bool> predicate);
    }

    public interface IFluentGroupConfiguration
    {
        IFluentGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
    }
}
