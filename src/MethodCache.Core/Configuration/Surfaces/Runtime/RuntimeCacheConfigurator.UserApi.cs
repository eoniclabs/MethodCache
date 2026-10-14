using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Infrastructure;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

internal sealed partial class RuntimeCacheConfigurator
{
    public async Task UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configure);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKnown(methodId);
        var builder = new CacheEntryOptions.Builder();
        configure(builder);
        var draft = CachePolicyBuilderFactory.ForMethod(methodId, builder.Build()).Build(methodId);
        await _store.UpsertAsync(methodId, draft.Policy, draft.Fields, metadata).ConfigureAwait(false);
        await WaitForInvalidation(methodId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string methodId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKnown(methodId);
        await _store.RemoveAsync(methodId).ConfigureAwait(false);
        await WaitForInvalidation(methodId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var methods = _store.GetSnapshots().Select(static snapshot => snapshot.MethodId).ToArray();
        await _store.ClearAsync().ConfigureAwait(false);
        await Task.WhenAll(methods.Select(method => WaitForInvalidation(method, cancellationToken))).ConfigureAwait(false);
    }

    public Task<IReadOnlyCollection<RuntimeOverride>> GetOverridesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_store.DescribeOverrides());
    }

    private void ValidateKnown(string methodId)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodId);
        if (_registry != null && (MethodIds.IsDefaults(methodId) || !_registry.GetAllPolicies().Any(policy => policy.MethodId == methodId && policy.Contributions.Any(static contribution => contribution.SourceId == "attributes"))))
            throw new ArgumentException($"Unknown MethodCache method '{methodId}'.", nameof(methodId));
        if (_registry is PolicyRegistry registry) _ = registry.GetHandle(methodId);
    }

    private Task WaitForInvalidation(string methodId, CancellationToken token)
        => _registry is PolicyRegistry registry ? registry.Invalidation(methodId).WaitAsync(token) : Task.CompletedTask;
}
