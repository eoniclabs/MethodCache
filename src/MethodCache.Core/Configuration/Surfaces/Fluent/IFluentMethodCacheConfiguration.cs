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

        IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, TResult>> method);

        IFluentMethodConfiguration Method(Expression<Func<TService, Task>> method);

        IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, Task<TResult>>> method);

        IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, ValueTask<TResult>>> method);
    }

    public interface IFluentMethodConfiguration
    {
        IFluentMethodConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
        IFluentMethodConfiguration WithGroup(string groupName);
        IFluentMethodConfiguration WithVersion(int version);
    }

    /// <summary>
    /// Configures one method whose result type is <typeparamref name="TResult"/> (for async methods, the awaited type).
    /// </summary>
    public interface IFluentMethodConfiguration<TResult> : IFluentMethodConfiguration
    {
        new IFluentMethodConfiguration<TResult> Configure(Action<CacheEntryOptions.Builder> configure);
        new IFluentMethodConfiguration<TResult> WithGroup(string groupName);
        new IFluentMethodConfiguration<TResult> WithVersion(int version);

        /// <summary>
        /// Stores a result only when <paramref name="predicate"/> returns true, e.g. <c>CacheWhen(r =&gt; r.Count &gt; 0)</c>.
        /// A rejected result is still returned to the caller, the entry it would have replaced stays, and the next call
        /// runs the method again. The predicate runs once per load, never on a cache hit.
        /// </summary>
        IFluentMethodConfiguration<TResult> CacheWhen(Func<TResult, bool> predicate);
    }

    public interface IFluentGroupConfiguration
    {
        IFluentGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
    }
}
