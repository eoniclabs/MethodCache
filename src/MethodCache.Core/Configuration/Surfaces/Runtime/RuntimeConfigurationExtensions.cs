using MethodCache.Policies;

namespace MethodCache;

// Internal pipeline operations stay available to implementation tests, never to application callers.
internal static class RuntimeConfigurationExtensions
{
    internal static Task UpsertAsync(this IRuntimeCacheConfigurator configuration, string id, CachePolicy policy,
        CachePolicyFields fields = CachePolicyFields.None, RuntimeOverrideMetadata? metadata = null)
        => ((IRuntimeConfiguration)configuration).UpsertAsync(id, policy, fields, metadata);
    internal static Task UpsertBatchAsync(this IRuntimeCacheConfigurator configuration, IEnumerable<RuntimeOverrideEntry> overrides)
        => ((IRuntimeConfiguration)configuration).UpsertBatchAsync(overrides);
    internal static Task ApplyAsync(this IRuntimeCacheConfigurator configuration, Action<IFluentMethodCacheConfiguration> configure)
        => ((IRuntimeConfiguration)configuration).ApplyAsync(configure);
    internal static Task<PolicySnapshot?> GetOverrideAsync(this IRuntimeCacheConfigurator configuration, string id)
        => ((IRuntimeConfiguration)configuration).GetOverrideAsync(id);
    internal static IAsyncEnumerable<PolicyChange> WatchAsync(this IRuntimeCacheConfigurator configuration, CancellationToken token = default)
        => ((IRuntimeConfiguration)configuration).WatchAsync(token);
}
