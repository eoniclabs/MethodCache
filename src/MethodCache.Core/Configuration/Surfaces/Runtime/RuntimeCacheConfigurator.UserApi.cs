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
        var options = BuildOverride(configure, metadata);
        var draft = Prepare(methodId, options);
        await ApplyAsync(methodId, draft, metadata, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpsertGroupAsync(string group, Action<CacheEntryOptions.Builder> configure,
        RuntimeOverrideMetadata? metadata, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(configure);
        cancellationToken.ThrowIfCancellationRequested();
        await UpsertEachAsync(GroupMembers(group), BuildOverride(configure, metadata), metadata, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpsertAllAsync(Action<CacheEntryOptions.Builder> configure, RuntimeOverrideMetadata? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configure);
        cancellationToken.ThrowIfCancellationRequested();
        await UpsertEachAsync(CachedMethods().Order(StringComparer.Ordinal).ToArray(), BuildOverride(configure, metadata), metadata, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RemoveGroupAsync(string group, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        cancellationToken.ThrowIfCancellationRequested();
        var members = GroupMembers(group);
        IEnumerable<string> overridden = _store.GetSnapshots().Select(static snapshot => snapshot.MethodId);
        if (Shared is { } shared) overridden = overridden.Concat(await shared.StoredMethodsAsync(cancellationToken).ConfigureAwait(false));
        var remove = overridden.Intersect(members, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var method in remove)
        {
            await RemoveAsync(method, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Puts the same override on each of <paramref name="methods"/>, after checking it fits every one of them, so a
    /// mistake changes nothing. One method at a time, so a failure leaves each method with its old or its new override.
    /// </summary>
    private async ValueTask UpsertEachAsync(IReadOnlyList<string> methods, CacheEntryOptions options, RuntimeOverrideMetadata? metadata,
        CancellationToken cancellationToken)
    {
        var drafts = methods.Select(method => (Method: method, Draft: Prepare(method, options))).ToArray();
        foreach (var (method, draft) in drafts)
        {
            await ApplyAsync(method, draft, metadata, cancellationToken).ConfigureAwait(false);
        }
    }

    private CacheEntryOptions BuildOverride(Action<CacheEntryOptions.Builder> configure, RuntimeOverrideMetadata? metadata)
    {
        var builder = new CacheEntryOptions.Builder();
        configure(builder);
        var options = builder.Build();
        if (options.Name != null)
        {
            throw new ArgumentException(
                "WithName names an application's own GetOrCreateAsync call in metrics and logs; a cached method is always named by its " +
                "method id, so an override can't set it. Remove WithName from the override.", nameof(configure));
        }

        if (metadata?.ExpiresAt is { } expiresAt && expiresAt <= _store.Time.GetUtcNow())
        {
            throw new ArgumentException(
                $"The override's ExpiresAt ({expiresAt:O}) has already passed, so it would be removed as soon as it was applied. " +
                "Set a time in the future, or leave ExpiresAt unset to keep the override until it is removed.", nameof(metadata));
        }

        return options;
    }

    /// <summary>The override for <paramref name="methodId"/>, checked against the method and the limits of the cache.</summary>
    private PolicyDraft Prepare(string methodId, CacheEntryOptions options)
    {
        var draft = CachePolicyBuilderFactory.ForMethod(methodId, options).Build(methodId);
        if (draft.Fields == CachePolicyFields.None)
        {
            throw new ArgumentException(
                "The override sets nothing, so it would change nothing. Set at least one setting, such as o.WithDuration(...) or " +
                "o.WithEnabled(false); to go back to the method's other settings, remove its override instead.", "configure");
        }

        if (_registry is PolicyRegistry registry)
        {
            if (registry.Resolver.FindOverrideContradiction(methodId, draft.Policy, draft.Fields) is { } contradiction)
            {
                throw new ArgumentException(contradiction, "configure");
            }

            if (registry.Resolver.FindOverrideTagProblem(methodId, draft.Policy, draft.Fields) is { } problem)
            {
                throw new ArgumentException(
                    $"The tag '{problem.Tag}' in the override reaches the cached method '{problem.MethodId}', but {problem.Reason}.", "configure");
            }

            if (RefreshProblem(registry, methodId, draft) is { } refresh)
            {
                throw new ArgumentException(refresh, "configure");
            }
        }

        if (Shared != null && (draft.Fields & SharedRuntimeOverrides.FunctionFields) != CachePolicyFields.None)
        {
            throw new ArgumentException(
                "A runtime override applies on every instance through the distributed cache, so it can't set CacheWhen, " +
                "DurationFor, TagsFor or SlidingFor, which are functions. Set them in AddMethodCache, and change Duration, " +
                "SlidingExpiration, tags or the other settings at runtime.", "configure");
        }

        return draft;
    }

    /// <summary>
    /// Why the override would leave <paramref name="methodId"/> with a refresh window as long as its duration or longer,
    /// which would refresh every entry as soon as it is stored; null when it doesn't.
    /// </summary>
    private static string? RefreshProblem(PolicyRegistry registry, string methodId, PolicyDraft draft)
    {
        if ((draft.Fields & (CachePolicyFields.RefreshAhead | CachePolicyFields.Duration)) == CachePolicyFields.None) return null;
        var current = registry.GetPolicy(methodId);
        var below = current.Declaration ?? current.Policy;
        var refresh = draft.Fields.HasFlag(CachePolicyFields.RefreshAhead) ? draft.Policy.RefreshAhead : below.RefreshAhead;
        var duration = draft.Fields.HasFlag(CachePolicyFields.Duration) ? draft.Policy.Duration : below.Duration;
        if (refresh is not { } window || window <= TimeSpan.Zero || duration is not { } length || length <= TimeSpan.Zero || window < length) return null;
        // The options builder already refuses both in one override
        var from = draft.Fields.HasFlag(CachePolicyFields.RefreshAhead)
            ? "the Duration comes from the method's other settings"
            : "the RefreshAhead comes from the method's other settings";
        return $"With the override, '{methodId}' would refresh {window} before it expires but only be kept for {length} ({from}), " +
               "so every entry would be refreshed as soon as it was stored. Set a RefreshAhead shorter than the Duration.";
    }

    private async ValueTask ApplyAsync(string methodId, PolicyDraft draft, RuntimeOverrideMetadata? metadata, CancellationToken cancellationToken)
    {
        if (Shared is { } shared)
        {
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
            // The overrides stored for the cached methods, also ones this instance hasn't read yet, and the ones it has.
            // One method at a time, so a failure leaves each override either removed everywhere or still in place.
            var stored = await shared.StoredMethodsAsync(cancellationToken).ConfigureAwait(false);
            var overridden = stored.Concat(_store.GetSnapshots().Select(static snapshot => snapshot.MethodId))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (var method in overridden)
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
            var cached = CachedMethods();
            if (MethodIds.IsDefaults(methodId) || !cached.Contains(methodId))
                throw new ArgumentException(MethodIds.NotCached(methodId, cached), nameof(methodId));
        }

        if (_registry is PolicyRegistry registry) _ = registry.GetHandle(methodId);
    }

    /// <summary>The cached methods; without a registry, the methods that have an override.</summary>
    private HashSet<string> CachedMethods()
        => _registry == null
            ? _store.GetSnapshots().Select(static snapshot => snapshot.MethodId).ToHashSet(StringComparer.Ordinal)
            : CachedPolicies().Select(static policy => policy.MethodId).ToHashSet(StringComparer.Ordinal);

    private IEnumerable<PolicyResolutionResult> CachedPolicies()
        => _registry!.GetAllPolicies()
            .Where(static policy => !MethodIds.IsDefaults(policy.MethodId)
                && policy.Contributions.Any(static contribution => contribution.SourceId == PolicySourceIds.Attributes));

    /// <summary>The cached methods in <paramref name="group"/>, ordered by id.</summary>
    /// <exception cref="ArgumentException">No cached method is in the group.</exception>
    private string[] GroupMembers(string group)
    {
        if (_registry == null)
            throw new ArgumentException("Overrides for a group need the cached methods, which this configurator has no access to.", nameof(group));
        var groups = CachedPolicies()
            .Select(static policy => (policy.MethodId, Group: policy.Policy.Metadata.GetValueOrDefault("group")))
            .Where(static member => !string.IsNullOrWhiteSpace(member.Group))
            .ToArray();
        var members = groups.Where(member => string.Equals(member.Group, group, StringComparison.Ordinal))
            .Select(static member => member.MethodId).Order(StringComparer.Ordinal).ToArray();
        if (members.Length > 0) return members;
        var names = groups.Select(static member => member.Group!).Distinct(StringComparer.Ordinal).ToArray();
        var hint = MethodIds.Closest(group, names) is { } closest ? $"; did you mean '{closest}'" : names.Length == 0 ? "; no cached method is in a group" : "";
        throw new ArgumentException(
            $"No cached method is in the group '{group}'{hint}. Group names are case-sensitive; a method joins a group with " +
            "[Cache(Group = \"...\")], .WithGroup(\"...\") in mc.Method(...) or \"Group\" in its appsettings entry.", nameof(group));
    }

    private async Task WaitForInvalidation(string methodId, CancellationToken token)
    {
        if (_registry is not PolicyRegistry registry) return;
        var pending = registry.Invalidation(methodId);
        try { await pending.WaitAsync(token).ConfigureAwait(false); }
        finally { if (pending.IsCompleted) registry.ForgetInvalidation(methodId, pending); }
    }
}
