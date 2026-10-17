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
            return new PolicyResolver(registrations, provider.GetService<ILogger<PolicyResolver>>());
        });

        services.TryAddSingleton<IPolicyResolver>(provider => provider.GetRequiredService<PolicyResolver>());

        services.TryAddSingleton<PolicyRegistry>(provider =>
        {
            var resolver = provider.GetRequiredService<PolicyResolver>();
            return new PolicyRegistry(resolver, id =>
            {
                var manager = provider.GetRequiredService<ICacheManager>();
                var memory = manager as MethodCache.Extensibility.InMemoryCacheManager;
                return memory?.InvalidateMethodAsync(id) ?? Task.CompletedTask;
            });
        });

        services.TryAddSingleton<IPolicyRegistry>(provider => provider.GetRequiredService<PolicyRegistry>());
        services.TryAddSingleton<PolicyDiagnosticsService>();
    }
}
