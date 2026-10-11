using System.Linq.Expressions;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache;

/// <summary>
/// Names the method to override with an expression instead of its id, for example
/// <c>configurator.UpsertAsync&lt;IUserService, User&gt;(s =&gt; s.GetUserAsync(0), o =&gt; o.WithDuration(...))</c>.
/// The arguments in the expression are ignored.
/// </summary>
public static class RuntimeCacheConfiguratorExtensions
{
    /// <summary>Removes the runtime override of the async method <paramref name="methodExpression"/> calls.</summary>
    public static Task RemoveAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId(methodExpression));

    /// <summary>Removes the runtime override of the async method <paramref name="methodExpression"/> calls.</summary>
    public static Task RemoveAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task<TResult>>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId(methodExpression));

    /// <summary>Removes the runtime override of the method <paramref name="methodExpression"/> calls.</summary>
    public static Task RemoveAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, TResult>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId(methodExpression));

    /// <summary>Removes the runtime override of the void method <paramref name="methodExpression"/> calls.</summary>
    public static Task RemoveAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Action<TService>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId(methodExpression));

    /// <summary>Applies or replaces the runtime override of the async method <paramref name="methodExpression"/> calls.</summary>
    public static Task UpsertAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task>> methodExpression,
        Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId(methodExpression), configure, metadata);

    /// <summary>Applies or replaces the runtime override of the async method <paramref name="methodExpression"/> calls.</summary>
    public static Task UpsertAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task<TResult>>> methodExpression,
        Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId(methodExpression), configure, metadata);

    /// <summary>Applies or replaces the runtime override of the method <paramref name="methodExpression"/> calls.</summary>
    public static Task UpsertAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, TResult>> methodExpression,
        Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId(methodExpression), configure, metadata);

    /// <summary>Applies or replaces the runtime override of the void method <paramref name="methodExpression"/> calls.</summary>
    public static Task UpsertAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Action<TService>> methodExpression,
        Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId(methodExpression), configure, metadata);

    private static string BuildMethodId(LambdaExpression expression)
    {
        if (expression?.Body is not MethodCallExpression call)
        {
            throw new ArgumentException("Expression must target a method call.", nameof(expression));
        }

        return MethodIds.For(call.Method);
    }
}
