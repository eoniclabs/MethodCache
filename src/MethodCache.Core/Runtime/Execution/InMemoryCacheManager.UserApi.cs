using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Infrastructure;
using MethodCache.Core.Telemetry;

namespace MethodCache.Extensibility;

internal partial class InMemoryCacheManager
{
    /// <inheritdoc />
    public ValueTask<T> GetOrCreateAsync<TState, T>(string key, TState state, Func<TState, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options, CancellationToken cancellationToken)
    {
        ValidateTarget(key, nameof(key));
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= CacheEntryOptions.Default;
        // A generated call's key is complete; an application key is framed with its version
        var cacheKey = options.Runtime != null ? key : MethodCacheKeys.VersionedKey(key, options.Version);
        _telemetry?.ClaimCustomRequest();
        if (options.Enabled != false && options.Runtime == null && options.CacheWhen == null && TryGetFastNode<T>(cacheKey, out var cached, out var cachedNode))
        {
            RecordHitTelemetry(CacheKeyLookup.MethodName, cachedNode);
            return new(cached!);
        }
        return GetOrCreateUserSlowAsync(key, cacheKey, state, factory, options, cancellationToken);
    }

    private ValueTask<T> GetOrCreateUserSlowAsync<TState, T>(string key, string cacheKey, TState state,
        Func<TState, CancellationToken, ValueTask<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken)
    {
        if (options.Enabled == false) return _telemetry?.Enabled == true
            ? new ValueTask<T>(_telemetry.BypassAsync(CacheKeyLookup.MethodName, key, () => factory(state, cancellationToken).AsTask())) : factory(state, cancellationToken);
        // A CacheWhen<T> that can never see this result type is a configuration error, reported before the factory runs
        CachePredicateCheck.EnsureMatches(options.CacheWhen, typeof(T), key);
        var policy = options.Runtime;
        if (policy == null)
        {
            var draft = CachePolicyBuilderFactory.FromOptions(options).Build(CacheKeyLookup.MethodName);
            policy = CacheRuntimePolicy.FromPolicy(CacheKeyLookup.MethodName, draft.Policy, draft.Fields);
        }
        return new ValueTask<T>(GetOrCreateFastAsync(cacheKey, policy.MethodId,
            () => factory(state, cancellationToken).AsTask(), policy, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, int? version = null, CancellationToken cancellationToken = default)
        => TryGetPreparedAsync<T>(MethodCacheKeys.StoredKey(key, version), cancellationToken);

    internal ValueTask<CacheLookupResult<T>> TryGetPreparedAsync<T>(string key, CancellationToken cancellationToken)
    {
        ValidateTarget(key, nameof(key));
        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetFresh<T>(key, _engine.Now, out var value, out var node, out _))
        {
            RecordHitTelemetry(CacheKeyLookup.MethodName, node);
            return new(CacheLookupResult<T>.Hit(value!));
        }
        return ReadDistributedLookupAsync<T>(key, cancellationToken);
    }

    internal CacheLookupResult<T> TryGetFreshForLookup<T>(string key)
        => TryGetFresh<T>(key, _engine.Now, out var value, out _, out _) ? CacheLookupResult<T>.Hit(value!) : CacheLookupResult<T>.Miss;

    private async ValueTask<CacheLookupResult<T>> ReadDistributedLookupAsync<T>(string key, CancellationToken token)
    {
        var outcome = RequestResult.Error;
        try
        {
            var policy = _lookupPolicy?.Invoke(key) ?? CacheRuntimePolicy.Empty(CacheKeyLookup.MethodName);
            var result = await TryGetDistributedAsync<T>(key, CacheKeyLookup.MethodName,
                policy, token).ConfigureAwait(false);
            outcome = result.Found ? RequestResult.Hit : RequestResult.Miss;
            return result.Found ? CacheLookupResult<T>.Hit(result.Value!) : CacheLookupResult<T>.Miss;
        }
        finally { _telemetry?.Request("method", CacheKeyLookup.MethodName, outcome); }
    }

    /// <inheritdoc />
    public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, int? version = null, CancellationToken cancellationToken = default)
    {
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        return InvalidateTargetsAsync(ValidateTargets(keys).Select(key => MethodCacheKeys.StoredKey(key, version)), false, cancellationToken);
    }

    internal ValueTask<InvalidationResult> InvalidatePreparedKeysAsync(IEnumerable<string> keys, CancellationToken token)
        => InvalidateTargetsAsync(keys, false, token);

    /// <inheritdoc />
    public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken)
        => InvalidateTargetsAsync(tags, true, cancellationToken);

    private ValueTask<InvalidationResult> InvalidateTargetsAsync(IEnumerable<string> targets, bool tags, CancellationToken token)
    {
        var validated = ValidateTargets(targets);
        token.ThrowIfCancellationRequested();
        using var activity = _distributed == null ? _telemetry?.BeginInvalidation(tags ? "tag" : "key",
            keys: tags ? null : validated, tags: tags ? validated : null) : null;
        foreach (var target in validated)
        {
            if (tags) RemoveLocalTag(target);
            else RemoveLocal(target);
        }
        _telemetry?.Invalidations(tags ? "tag" : "key", "local", validated.Length);
        // Once local mutation occurs, storage recovery is owned by the cache, not the caller's token.
        if (_distributed == null) return new(new InvalidationResult { Requested = validated.Length, Local = validated.Length });
        var recovery = _distributed.InvalidateTargetsAsync(validated, tags);
        _ = recovery.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new(recovery.WaitAsync(token));
    }

    internal InvalidationQueues GetInvalidationQueues() => _distributed?.GetInvalidationQueues() ?? new(0, 0);

    internal Task<InvalidationResult> InvalidateMethodAsync(string methodId)
    {
        using var activity = _distributed == null ? _telemetry?.BeginInvalidation("method") : null;
        InvalidateMethodLocally(methodId);
        _telemetry?.Invalidations("method", "local", 1);
        return _distributed?.InvalidateMethodAsync(methodId) ?? Task.FromResult(new InvalidationResult { Requested = 1, Local = 1 });
    }

    /// <summary>
    /// The id of the cached method an entry belongs to, as <see cref="CachedMethodInfo.MethodId"/> gives it; null for a
    /// manual entry, which belongs to no method.
    /// </summary>
    internal static string? MethodIdOf(Core.Runtime.Memory.Node node)
        => node.Policy is EntryPolicy { Source.MethodId: var id } && id != CacheKeyLookup.MethodName ? id : null;

    private void InvalidateMethodLocally(string methodId)
    {
        foreach (var flight in _flights.Values.Concat<Flight>(_syncFlights.Values))
            if (flight.MethodId == methodId) flight.Invalidated = true;
        foreach (var node in _engine.Map.Values)
            if (node.Policy is EntryPolicy { Source: { } policy } && policy.MethodId == methodId) _engine.RemoveNode(node);
    }

    internal static string[] ValidateTargets(IEnumerable<string> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets) { ValidateTarget(target, nameof(targets)); result.Add(target); }
        return result.ToArray();
    }

    private static void ValidateTarget(string target, string name)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("A cache target cannot be null or whitespace.", name);
    }
}
