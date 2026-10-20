using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Infrastructure;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

internal sealed partial class RuntimeCacheConfigurator
{
    public async ValueTask UpsertAsync(string methodId, Action<CacheEntryOptions.Builder> configure,
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

    public async ValueTask RemoveAsync(string methodId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKnown(methodId);
        await _store.RemoveAsync(methodId).ConfigureAwait(false);
        await WaitForInvalidation(methodId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var methods = _store.GetSnapshots().Select(static snapshot => snapshot.MethodId).ToArray();
        await _store.ClearAsync().ConfigureAwait(false);
        await Task.WhenAll(methods.Select(method => WaitForInvalidation(method, cancellationToken))).ConfigureAwait(false);
    }

    public IReadOnlyList<RuntimeOverride> GetOverrides() => _store.DescribeOverrides();

    private void ValidateKnown(string methodId)
    {
        ArgumentException.ThrowIfNullOrEmpty(methodId);
        if (_registry != null)
        {
            var cached = _registry.GetAllPolicies()
                .Where(static policy => policy.Contributions.Any(static contribution => contribution.SourceId == PolicySourceIds.Attributes))
                .Select(static policy => policy.MethodId)
                .ToHashSet(StringComparer.Ordinal);
            if (MethodIds.IsDefaults(methodId) || !cached.Contains(methodId))
                throw new ArgumentException(MethodIds.NotCached(methodId, cached), nameof(methodId));
        }

        if (_registry is PolicyRegistry registry) _ = registry.GetHandle(methodId);
    }

    private async Task WaitForInvalidation(string methodId, CancellationToken token)
    {
        if (_registry is not PolicyRegistry registry) return;
        var pending = registry.Invalidation(methodId);
        try { await pending.WaitAsync(token).ConfigureAwait(false); }
        finally { if (pending.IsCompleted) registry.ForgetInvalidation(methodId, pending); }
    }
}
