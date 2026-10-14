using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using MethodCache.Core.Configuration.Surfaces.Fluent;
using MethodCache.Infrastructure;

namespace MethodCache.OpenTelemetry;

internal partial class TelemetryCacheManager
{
    public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CacheContext, CancellationToken, ValueTask<T>> factory,
        CacheEntryOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new CacheEntryOptions.Builder().Build();
        if (options.Enabled == false) return factory(new CacheContext(key), cancellationToken);
        var policy = options.Runtime;
        if (policy == null)
        {
            var draft = CachePolicyBuilderFactory.FromOptions(options).Build(CacheKeyLookup.MethodName);
            policy = CacheRuntimePolicy.FromPolicy(CacheKeyLookup.MethodName, draft.Policy, draft.Fields);
        }
        var prepared = key;
        return new(GetOrCreateFastAsync(prepared, policy.MethodId,
            () => factory(new CacheContext(key), cancellationToken).AsTask(), policy, cancellationToken));
    }

    public async ValueTask<CacheLookupResult<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartCacheOperation(CacheKeyLookup.MethodName, TracingConstants.Operations.Get);
        _activitySource.SetCacheKey(activity, key);
        var lookup = await _innerManager.TryGetAsync<T>(key, cancellationToken).ConfigureAwait(false);
        _activitySource.SetCacheHit(activity, lookup.Found);
        return lookup;
    }

    public ValueTask<InvalidationResult> InvalidateKeysAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
        => InvalidateTargetsAsync(keys, false, cancellationToken);

    public ValueTask<InvalidationResult> InvalidateTagsAsync(IEnumerable<string> tags, CancellationToken cancellationToken)
        => InvalidateTargetsAsync(tags, true, cancellationToken);

    private async ValueTask<InvalidationResult> InvalidateTargetsAsync(IEnumerable<string> targets, bool tags, CancellationToken token)
    {
        var validated = MethodCache.Extensibility.InMemoryCacheManager.ValidateTargets(targets);
        token.ThrowIfCancellationRequested();
        using var activity = _activitySource.StartCacheOperation(tags ? "InvalidateTags" : "InvalidateKeys", TracingConstants.Operations.Delete);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return tags ? await _innerManager.InvalidateTagsAsync(validated, token).ConfigureAwait(false)
                : await _innerManager.InvalidateKeysAsync(validated, token).ConfigureAwait(false);
        }
        catch (Exception exception) { _activitySource.SetCacheError(activity, exception); throw; }
        finally { _meterProvider.RecordOperationDuration("Invalidate", Stopwatch.GetElapsedTime(start).TotalMilliseconds); }
    }
}
