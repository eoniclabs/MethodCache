using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MethodCache.Core.PolicyPipeline.Resolution;

internal static class PolicyRegistrationExtensions
{
    public static void EnsurePolicyServices(this IServiceCollection services)
    {
        services.TryAddSingleton<PolicyResolver>(provider =>
        {
            var registrations = provider.GetServices<PolicySourceRegistration>();
            // Read when the resolver is built, after AddMethodCache recorded every decorated method's parameters
            var parameters = provider.GetService<MethodCacheServiceCollectionExtensions.GeneratedParameters>();
            // The cache manager that holds the method results, read when the policies load: the built-in one keeps an
            // entry at most MaxExpiration, and an application's own manager, even one registered later, has its own limits
            var limits = new PolicyLimits(
                () => provider.GetService<ICacheManager>() is MethodCache.Extensibility.InMemoryCacheManager memory ? memory.MaxExpiration : null,
                parameters?.ResultTypes);
            return new PolicyResolver(registrations, provider.GetService<ILogger<PolicyResolver>>(), parameters?.Names, limits, parameters?.DescribedMethods);
        });

        services.TryAddSingleton<IPolicyResolver>(provider => provider.GetRequiredService<PolicyResolver>());

        services.TryAddSingleton<PolicyRegistry>(provider =>
        {
            var resolver = provider.GetRequiredService<PolicyResolver>();
            return new PolicyRegistry(resolver, id =>
            {
                var memory = provider.GetService<MethodCache.Extensibility.InMemoryCacheManager>();
                return memory?.InvalidateMethodAsync(id) ?? Task.CompletedTask;
            }, () => MethodCache.Core.Configuration.Surfaces.Runtime.SharedRuntimeOverrides.From(provider),
                id => provider.GetService<MethodCache.Extensibility.InMemoryCacheManager>()?.InvalidateMethodHere(id));
        });

        services.TryAddSingleton<IPolicyRegistry>(provider => provider.GetRequiredService<PolicyRegistry>());
        services.TryAddSingleton<PolicyDiagnosticsService>();
    }
}
