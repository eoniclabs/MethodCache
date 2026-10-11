using System.Linq.Expressions;
using MethodCache.Core.Options;

namespace MethodCache.Core.Configuration.Surfaces.Fluent
{
    public interface IFluentMethodCacheConfiguration
    {
        /// <summary>
        /// Configures defaults for every cached method, including methods that only have <c>[Cache]</c>. Settings a
        /// method or its group sets themselves win.
        /// </summary>
        IFluentMethodCacheConfiguration DefaultPolicy(Action<CacheEntryOptions.Builder> configure);

        /// <summary>
        /// Configures the methods of one service; these settings win over the method's <c>[Cache]</c> attribute.
        /// </summary>
        IFluentServiceConfiguration<TService> ForService<TService>();

        /// <summary>
        /// Configures defaults for the methods in group <paramref name="name"/>, which they join with
        /// <c>[Cache("name")]</c> or <see cref="IFluentMethodConfiguration.WithGroup"/>. Settings a method sets itself
        /// win; these win over <see cref="DefaultPolicy"/>.
        /// </summary>
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
