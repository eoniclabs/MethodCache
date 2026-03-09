using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Resolution;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.Options;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

internal sealed class RuntimeCacheConfigurator : IRuntimeCacheConfigurator
{
    private readonly RuntimePolicyStore _store;

    public RuntimeCacheConfigurator(RuntimePolicyStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public Task UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure, RuntimeOverrideMetadata? metadata = null)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new CacheEntryOptions.Builder();
        configure(builder);
        var options = builder.Build();
        var policyBuilder = CachePolicyBuilderFactory.FromOptions(options);
        if (options.Predicate != null)
        {
            policyBuilder.RequireIdempotent();
        }

        var draft = policyBuilder.Build(methodId);
        return _store.UpsertAsync(methodId, draft.Policy, draft.Fields, metadata);
    }

    public Task UpsertAsync(string methodId, Action<CachePolicyBuilder> configure, RuntimeOverrideMetadata? metadata = null)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var builder = new CachePolicyBuilder();
        configure(builder);
        var draft = builder.Build(methodId);
        return _store.UpsertAsync(methodId, draft.Policy, draft.Fields, metadata);
    }

    public Task UpsertAsync(string methodId, CachePolicy policy, CachePolicyFields fields = CachePolicyFields.None, RuntimeOverrideMetadata? metadata = null)
        => _store.UpsertAsync(methodId, policy, fields, metadata);

    public async Task UpsertBatchAsync(IEnumerable<RuntimeOverrideEntry> overrides)
    {
        if (overrides == null)
        {
            throw new ArgumentNullException(nameof(overrides));
        }

        foreach (var entry in overrides)
        {
            await _store.UpsertAsync(entry.MethodId, entry.Policy, entry.Fields, entry.Metadata).ConfigureAwait(false);
        }
    }

    public Task ApplyAsync(Action<IFluentMethodCacheConfiguration> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var fluent = new FluentMethodCacheConfiguration();
        configure(fluent);
        var drafts = fluent.BuildMethodPolicies();

        return UpsertBatchAsync(drafts.Select(d => new RuntimeOverrideEntry(d.MethodId, d.Policy, d.Fields)));
    }

    public Task RemoveAsync(string methodId) => _store.RemoveAsync(methodId);

    public Task ClearAsync() => _store.ClearAsync();

    public Task<IReadOnlyCollection<PolicySnapshot>> GetOverridesAsync()
        => Task.FromResult(_store.GetSnapshots());

    public Task<PolicySnapshot?> GetOverrideAsync(string methodId)
        => Task.FromResult(_store.GetSnapshot(methodId));

    public IAsyncEnumerable<PolicyChange> WatchAsync(CancellationToken cancellationToken = default)
        => _store.WatchAsync(cancellationToken);
}
