using System.Linq.Expressions;
using MethodCache.Core.Options;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

public static class RuntimeCacheConfiguratorExtensions
{
    public static Task RemoveAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId<TService>(methodExpression));

    public static Task RemoveAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task<TResult>>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId<TService>(methodExpression));

    public static Task RemoveAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Action<TService>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId<TService>(methodExpression));

    public static Task RemoveAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, TResult>> methodExpression)
        => configurator.RemoveAsync(BuildMethodId<TService>(methodExpression));

    public static Task UpsertAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task>> methodExpression,
        Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId<TService>(methodExpression), configure, metadata);

    public static Task UpsertAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, Task<TResult>>> methodExpression,
        Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId<TService>(methodExpression), configure, metadata);

    public static Task UpsertAsync<TService>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Action<TService>> methodExpression,
        Action<CachePolicyBuilder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId<TService>(methodExpression), configure, metadata);

    public static Task UpsertAsync<TService, TResult>(
        this IRuntimeCacheConfigurator configurator,
        Expression<Func<TService, TResult>> methodExpression,
        Action<CachePolicyBuilder> configure,
        RuntimeOverrideMetadata? metadata = null)
        => configurator.UpsertAsync(BuildMethodId<TService>(methodExpression), configure, metadata);

    private static string BuildMethodId<TService>(LambdaExpression expression)
    {
        if (expression?.Body is not MethodCallExpression call)
        {
            throw new ArgumentException("Expression must target a method call.", nameof(expression));
        }

        var typeName = typeof(TService).FullName ?? typeof(TService).Name;
        typeName = typeName.Replace('+', '.');
        return $"{typeName}.{call.Method.Name}";
    }
}
