using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Infrastructure;
using MethodCache.Core.Telemetry;

namespace MethodCache.Extensibility;

internal partial class InMemoryCacheManager
{
    /// <inheritdoc />
    public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options, CancellationToken cancellationToken)
    {
        ValidateTarget(key, nameof(key));
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= CacheEntryOptions.Default;
        var cacheKey = options.GetCacheKey(key);
        if (options.Enabled != false && options.Runtime == null && options.CacheWhen == null && TryGetFastNode<T>(cacheKey, out var cached, out var cachedNode))
        {
            RecordHitTelemetry(CacheKeyLookup.MethodName, cachedNode);
            return new(cached!);
        }
        return GetOrCreateUserSlowAsync(key, cacheKey, factory, options, cancellationToken);
    }

    private ValueTask<T> GetOrCreateUserSlowAsync<T>(string key, string cacheKey,
        Func<CacheContext, CancellationToken, ValueTask<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken)
    {
        var context = new CacheContext(key);
        if (options.Enabled == false) return _telemetry?.Enabled == true
            ? new ValueTask<T>(_telemetry.BypassAsync(CacheKeyLookup.MethodName, key, () => factory(context, cancellationToken).AsTask())) : factory(context, cancellationToken);
        var policy = options.Runtime;
        if (policy == null)
        {
            var draft = CachePolicyBuilderFactory.FromOptions(options).Build(CacheKeyLookup.MethodName);
            policy = CacheRuntimePolicy.FromPolicy(CacheKeyLookup.MethodName, draft.Policy, draft.Fields);
        }
        return new ValueTask<T>(GetOrCreateFastAsync(cacheKey, policy.MethodId,
            () => factory(context, cancellationToken).AsTask(), policy, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken)
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
            var result = await TryGetDistributedAsync<T>(key, CacheKeyLookup.MethodName,
                CacheRuntimePolicy.Empty(CacheKeyLookup.MethodName), token).ConfigureAwait(false);
            outcome = result.Found ? RequestResult.Hit : RequestResult.Miss;
            return result.Found ? CacheLookupResult<T>.Hit(result.Value!) : CacheLookupResult<T>.Miss;
        }
        finally { _telemetry?.Request("method", CacheKeyLookup.MethodName, outcome); }
    }

    /// <inheritdoc />
    public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
        => InvalidateTargetsAsync(keys, false, cancellationToken);

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

    internal Task InvalidateMethodAsync(string methodId)
    {
        using var activity = _distributed == null ? _telemetry?.BeginInvalidation("method") : null;
        InvalidateMethodLocally(methodId);
        _telemetry?.Invalidations("method", "local", 1);
        return _distributed?.InvalidateMethodAsync(methodId) ?? Task.CompletedTask;
    }

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
