namespace MethodCache;

/// <summary>Changes method policies locally without changing other nodes' configuration.</summary>
public interface IRuntimeCacheConfigurator
{
    /// <summary>Applies a method override and publishes its local generation before completion.</summary>
    Task UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata = null, CancellationToken cancellationToken = default);
    /// <summary>Removes a method override and invalidates previous shared values before L2 reuse.</summary>
    Task RemoveAsync(string methodId, CancellationToken cancellationToken = default);
    /// <summary>Removes all method overrides.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
    /// <summary>Gets immutable descriptions of active local overrides.</summary>
    Task<IReadOnlyCollection<RuntimeOverride>> GetOverridesAsync(CancellationToken cancellationToken = default);
}
