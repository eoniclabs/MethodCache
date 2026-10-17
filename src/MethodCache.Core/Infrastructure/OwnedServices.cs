using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MethodCache.Infrastructure;

internal static class OwnedServices
{
    internal const string Key = "MethodCache";

    internal static void AddOptions(IServiceCollection services)
    {
        services.AddOptions<DistributedCacheOptions>();
        services.TryAddKeyedSingleton<DistributedCacheOptions>(Key, static (provider, _) =>
            provider.GetRequiredService<IOptions<DistributedCacheOptions>>().Value.Freeze());
    }
}
