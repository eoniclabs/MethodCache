using System.Linq.Expressions;

namespace MethodCache
{
    /// <summary>
    /// Configures cache policies in code, passed to <c>AddMethodCache(config =&gt; ...)</c>. These settings win over
    /// <c>[Cache]</c> attributes, and lose to <c>appsettings.json</c> and runtime overrides.
    /// </summary>
    internal interface IFluentMethodCacheConfiguration
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
        /// <c>[Cache(Group = "name")]</c> or <see cref="IFluentMethodConfiguration{TResult}.WithGroup"/>. Settings a method sets itself
        /// win; these win over <see cref="DefaultPolicy"/>.
        /// </summary>
        IFluentGroupConfiguration ForGroup(string name);
    }

    /// <summary>
    /// Picks methods of <typeparamref name="TService"/> to configure, pointing at each with an expression such as
    /// <c>s =&gt; s.GetAsync(Any&lt;int&gt;.Value)</c>. Use <see cref="Any{T}.Value"/> for arguments.
    /// </summary>
    internal interface IFluentServiceConfiguration<TService>
    {
        /// <summary>Configures a synchronous method that returns <typeparamref name="TResult"/>.</summary>
        /// <param name="method">An expression calling the method, such as <c>s =&gt; s.Get(Any&lt;int&gt;.Value)</c>.</param>
        /// <returns>The method's configuration.</returns>
        IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, TResult>> method);

        /// <summary>Configures a method that returns <see cref="Task{TResult}"/>.</summary>
        /// <param name="method">An expression calling the method, such as <c>s =&gt; s.GetAsync(Any&lt;int&gt;.Value)</c>.</param>
        /// <returns>The method's configuration.</returns>
        IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, Task<TResult>>> method);

        /// <summary>Configures a method that returns <see cref="ValueTask{TResult}"/>.</summary>
        /// <param name="method">An expression calling the method, such as <c>s =&gt; s.GetAsync(Any&lt;int&gt;.Value)</c>.</param>
        /// <returns>The method's configuration.</returns>
        IFluentMethodConfiguration<TResult> Method<TResult>(Expression<Func<TService, ValueTask<TResult>>> method);
    }

    /// <summary>
    /// Configures one cached method selected through <see cref="MethodCacheBuilder"/>, whose result type is
    /// <typeparamref name="TResult"/> (for async methods, the awaited type).
    /// </summary>
    public interface IFluentMethodConfiguration<TResult>
    {
        /// <summary>Sets the method's cache options, such as duration and tags.</summary>
        /// <param name="configure">Sets options on the builder.</param>
        /// <returns>This configuration, for chaining.</returns>
        IFluentMethodConfiguration<TResult> Configure(Action<CacheEntryOptions.Builder> configure);
        /// <summary>
        /// Puts the method in a group, so it picks up the defaults set with
        /// <see cref="MethodCacheBuilder.Group"/>.
        /// </summary>
        /// <param name="groupName">The group's name.</param>
        /// <returns>This configuration, for chaining.</returns>
        IFluentMethodConfiguration<TResult> WithGroup(string groupName);
        /// <summary>
        /// Stores a result only when <paramref name="predicate"/> returns true, e.g. <c>CacheWhen(r =&gt; r.Count &gt; 0)</c>.
        /// A rejected result is still returned to the caller, the entry it would have replaced stays, and the next call
        /// runs the method again. The predicate runs once per load, never on a cache hit.
        /// </summary>
        IFluentMethodConfiguration<TResult> CacheWhen(Func<TResult, bool> predicate);
    }

    /// <summary>
    /// Configures defaults for the methods in a group, returned by <see cref="IFluentMethodCacheConfiguration.ForGroup"/>.
    /// </summary>
    internal interface IFluentGroupConfiguration
    {
        /// <summary>Sets cache options for every method in the group; settings a method sets itself win.</summary>
        /// <param name="configure">Sets options on the builder.</param>
        /// <returns>This configuration, for chaining.</returns>
        IFluentGroupConfiguration Configure(Action<CacheEntryOptions.Builder> configure);
    }
}
