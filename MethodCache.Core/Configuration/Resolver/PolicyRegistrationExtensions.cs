using System.Collections.Generic;
using MethodCache.Abstractions.Registry;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration.Registry;
using MethodCache.Core.Configuration.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MethodCache.Core.Configuration.Resolver;

public static class PolicyRegistrationExtensions
{
    public static void EnsurePolicyServices(this IServiceCollection services)
    {
        services.TryAddSingleton<PolicyResolver>(provider =>
        {
            var registrations = provider.GetServices<PolicySourceRegistration>();
            return new PolicyResolver(registrations);
        });

        services.TryAddSingleton<IPolicyResolver>(provider => provider.GetRequiredService<PolicyResolver>());

        services.TryAddSingleton<PolicyRegistry>(provider =>
        {
            var resolver = provider.GetRequiredService<PolicyResolver>();
            var registrations = provider.GetServices<PolicySourceRegistration>();
            return new PolicyRegistry(resolver, registrations);
        });

        services.TryAddSingleton<IPolicyRegistry>(provider => provider.GetRequiredService<PolicyRegistry>());
        services.TryAddSingleton<PolicyDiagnosticsService>();
    }
}
