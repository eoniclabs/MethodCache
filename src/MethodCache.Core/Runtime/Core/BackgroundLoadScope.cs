using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// Gives a load that outlives its caller (a refresh-ahead, or a fail-safe load that goes on after its caller got
    /// the stale value) its own instance of a scoped or transient service, from a new DI scope that ends with the
    /// load. The instance of the request that started the load may be disposed with that request's scope while the
    /// load is still running.
    /// </summary>
    /// <remarks>
    /// The cache runs such a load inside <see cref="RunAsync{T}"/>. The generated loader passes its decorated instance
    /// through <see cref="DecoratorServices.ForLoad{TService}"/>, which takes the pending load, creates the scope and
    /// returns the implementation resolved from it. Singletons, services the application created itself and calls
    /// outside such a load keep their own instance.
    /// </remarks>
    internal static class BackgroundLoadScope
    {
        private static readonly AsyncLocal<Load?> Pending = new();

        // The decorators of scoped and transient registrations, with how to resolve their implementation in a new scope
        private static readonly ConditionalWeakTable<object, Target> Targets = new();

        /// <summary>Records how to create a new instance of <paramref name="decorator"/>'s implementation.</summary>
        public static void Register(object decorator, IServiceScopeFactory scopeFactory, Type serviceType, object innerKey)
            => Targets.AddOrUpdate(decorator, new Target(scopeFactory, serviceType, innerKey));

        /// <summary>
        /// Runs <paramref name="factory"/> so the first generated loader it calls resolves its service from a new
        /// scope, and disposes that scope when the load ends.
        /// </summary>
        public static async Task<T> RunAsync<T>(Func<Task<T>> factory)
        {
            var load = new Load();

            // An async method restores its caller's AsyncLocal values, so this only reaches the factory
            Pending.Value = load;
            try
            {
                return await factory().ConfigureAwait(false);
            }
            finally
            {
                if (load.Scope is { } scope)
                {
                    await scope.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        public static TService ForLoad<TService>(object decorator, TService service)
            where TService : class
        {
            var load = Pending.Value;
            if (load == null)
            {
                return service;
            }

            // Only the loader the cache called takes the load; cached services it calls in turn share its scope
            Pending.Value = null;
            if (!Targets.TryGetValue(decorator, out var target))
            {
                return service;
            }

            var scope = target.ScopeFactory.CreateAsyncScope();
            load.Scope = scope;
            return (TService)scope.ServiceProvider.GetRequiredKeyedService(target.ServiceType, target.InnerKey);
        }

        private sealed class Load
        {
            public AsyncServiceScope? Scope { get; set; }
        }

        private sealed record Target(IServiceScopeFactory ScopeFactory, Type ServiceType, object InnerKey);
    }
}
