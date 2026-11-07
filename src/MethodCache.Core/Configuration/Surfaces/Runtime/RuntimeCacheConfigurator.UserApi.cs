using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Infrastructure;
using MethodCache.Policies;

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
        if (_registry is PolicyRegistry registry)
        {
            if (registry.Resolver.FindOverrideContradiction(methodId, draft.Policy, draft.Fields) is { } contradiction)
            {
                throw new ArgumentException(contradiction, nameof(configure));
            }

            if (registry.Resolver.FindOverrideTagProblem(methodId, draft.Policy, draft.Fields) is { } problem)
            {
                throw new ArgumentException(
                    $"The tag '{problem.Tag}' in the override reaches the cached method '{problem.MethodId}', but {problem.Reason}.", nameof(configure));
            }
        }

        if (Shared is { } shared)
        {
            if ((draft.Fields & SharedRuntimeOverrides.FunctionFields) != CachePolicyFields.None)
            {
                throw new ArgumentException(
                    "A runtime override applies on every instance through the distributed cache, so it can't set CacheWhen, " +
                    "DurationFor, TagsFor or SlidingFor, which are functions. Set them in AddMethodCache, and change Duration, " +
                    "SlidingExpiration, tags or the other settings at runtime.", nameof(configure));
            }

            await shared.UpsertAsync(methodId, draft.Policy, draft.Fields, metadata, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _store.UpsertAsync(methodId, draft.Policy, draft.Fields, metadata).ConfigureAwait(false);
        }

        await WaitForInvalidation(methodId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(string methodId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKnown(methodId);
        if (Shared is { } shared) await shared.RemoveAsync(methodId, cancellationToken).ConfigureAwait(false);
        else await _store.RemoveAsync(methodId).ConfigureAwait(false);
        await WaitForInvalidation(methodId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Shared is { } shared)
        {
            // One method at a time, so a failure leaves each override either removed everywhere or still in place
            foreach (var method in _store.GetSnapshots().Select(static snapshot => snapshot.MethodId).Order(StringComparer.Ordinal))
            {
                await shared.RemoveAsync(method, cancellationToken).ConfigureAwait(false);
                await WaitForInvalidation(method, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var methods = _store.GetSnapshots().Select(static snapshot => snapshot.MethodId).ToArray();
        await _store.ClearAsync().ConfigureAwait(false);
        await Task.WhenAll(methods.Select(method => WaitForInvalidation(method, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>The overrides every instance applies, once the policies are read and only with a backplane; otherwise null.</summary>
    private SharedRuntimeOverrides? Shared
    {
        get
        {
            if (_shared is not { Enabled: true }) return null;
            // Reading the policies starts the shared overrides, and says whether they apply
            _ = _registry?.GetAllPolicies();
            return _registry is PolicyRegistry { SharesOverrides: true } ? _shared : null;
        }
    }

    public IReadOnlyList<RuntimeOverride> GetOverrides()
    {
        // A starting instance reads the overrides every instance applies first. Policies that fail to load fail every
        // cached call with that error; this only lists what the instance has.
        try { _ = Shared; }
        catch (InvalidOperationException) { }

        return _store.DescribeOverrides();
    }

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
