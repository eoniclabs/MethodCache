using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.Configuration.Fluent;
using MethodCache.Core.Configuration.Sources;

namespace MethodCache.Core.Configuration.RuntimeConfiguration
{
    public interface IRuntimeCacheConfigurator
    {
        Task ApplyFluentAsync(Action<IFluentMethodCacheConfiguration> configure, CancellationToken cancellationToken = default);
        Task ApplyOverridesAsync(IEnumerable<MethodCacheConfigEntry> overrides, CancellationToken cancellationToken = default);
        Task ClearOverridesAsync(CancellationToken cancellationToken = default);
        Task<bool> RemoveOverrideAsync(string serviceType, string methodName, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<MethodCacheConfigEntry>> GetOverridesAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<MethodCacheConfigEntry>> GetEffectiveConfigurationAsync(CancellationToken cancellationToken = default);
    }
}
