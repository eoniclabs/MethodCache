using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Infrastructure
{
    /// <summary>
    /// Gives a load that outlives its caller (a refresh-ahead, or a fail-safe load that goes on after its caller got
    /// the stale value) its own instances of scoped and transient services, from new DI scopes that end with the
    /// load. The instances of the request that started the load may be disposed with that request's scope while the
    /// load is still running.
    /// </summary>
    /// <remarks>
    /// The cache runs such a load inside <see cref="RunAsync{T}"/>. Each generated loader passes its decorated instance
    /// through <see cref="DecoratorServices.ForLoad{TService}"/>; for a scoped or transient registration that
    /// creates a scope for the load and returns the implementation resolved from it. Every scope is disposed when the
    /// load ends, and a call the load leaves running after it ended keeps its own instance instead of getting a
    /// scope nobody would dispose. Singletons, services the application created itself and calls outside such a
    /// load keep their own instance.
    /// </remarks>
    internal static class BackgroundLoadScope
    {
        private static readonly AsyncLocal<Load?> Pending = new();

        /// <summary>
        /// Runs <paramref name="factory"/> so the generated loaders it calls resolve scoped and transient services
        /// from new scopes, and disposes those scopes when the load ends.
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
                await load.EndAsync().ConfigureAwait(false);
            }
        }

        public static TService ForLoad<TService>(BackgroundLoadTarget? target, TService service)
            where TService : class
        {
            if (target == null || Pending.Value is not { } load)
            {
                return service;
            }

            return load.TryCreateScope(target.ScopeFactory) is { } scope
                ? (TService)scope.ServiceProvider.GetRequiredKeyedService(target.ServiceType, target.InnerKey)
                : service;
        }

        private sealed class Load
        {
            private readonly object _sync = new();
            private List<AsyncServiceScope>? _scopes;
            private bool _ended;

            /// <summary>A new scope owned by this load, or null once the load has ended.</summary>
            public AsyncServiceScope? TryCreateScope(IServiceScopeFactory scopeFactory)
            {
                lock (_sync)
                {
                    if (_ended)
                    {
                        return null;
                    }

                    var scope = scopeFactory.CreateAsyncScope();
                    (_scopes ??= new List<AsyncServiceScope>()).Add(scope);
                    return scope;
                }
            }

            public async ValueTask EndAsync()
            {
                List<AsyncServiceScope>? scopes;
                lock (_sync)
                {
                    _ended = true;
                    scopes = _scopes;
                    _scopes = null;
                }

                if (scopes == null)
                {
                    return;
                }

                List<Exception>? errors = null;
                foreach (var scope in scopes)
                {
                    try
                    {
                        await scope.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        (errors ??= new List<Exception>()).Add(ex);
                    }
                }

                if (errors != null)
                {
                    throw new AggregateException(errors);
                }
            }
        }
    }

    /// <summary>How to create a new instance of a scoped or transient registration for a background load.</summary>
    internal sealed record BackgroundLoadTarget(IServiceScopeFactory ScopeFactory, Type ServiceType, object InnerKey);
}
