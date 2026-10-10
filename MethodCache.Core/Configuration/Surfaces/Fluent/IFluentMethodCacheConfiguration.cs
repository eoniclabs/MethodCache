using System.Linq.Expressions;
using MethodCache.Core.Options;

namespace MethodCache.Core.Configuration.Surfaces.Fluent
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
        IFluentMethodConfiguration WithVersion(int version);
    }

    public interface IFluentGroupConfiguration
    {
        IFluentGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
    }
}
