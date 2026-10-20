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
            var parameterNames = provider.GetService<MethodCacheServiceCollectionExtensions.GeneratedParameters>()?.Names;
            return new PolicyResolver(registrations, provider.GetService<ILogger<PolicyResolver>>(), parameterNames);
        });

        services.TryAddSingleton<IPolicyResolver>(provider => provider.GetRequiredService<PolicyResolver>());

        services.TryAddSingleton<PolicyRegistry>(provider =>
        {
            var resolver = provider.GetRequiredService<PolicyResolver>();
            return new PolicyRegistry(resolver, id =>
            {
                var memory = provider.GetService<MethodCache.Extensibility.InMemoryCacheManager>();
                return memory?.InvalidateMethodAsync(id) ?? Task.CompletedTask;
            });
        });

        services.TryAddSingleton<IPolicyRegistry>(provider => provider.GetRequiredService<PolicyRegistry>());
        services.TryAddSingleton<PolicyDiagnosticsService>();
    }
}
