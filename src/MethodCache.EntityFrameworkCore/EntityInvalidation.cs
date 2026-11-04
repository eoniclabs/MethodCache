using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace MethodCache.EntityFrameworkCore;

/// <summary>One context type's registration: its entity tags, the interceptor and the cache it invalidates.</summary>
internal class EntityInvalidation
{
    private readonly ICacheManager _cache;
    private readonly ILogger _logger;
    private readonly ConditionalWeakTable<IModel, BuiltMap> _maps = new();

    protected EntityInvalidation(Type contextType, EntityFrameworkCoreOptions options, ICacheManager cache, ILogger logger)
    {
        ContextType = contextType;
        Options = options;
        _cache = cache;
        _logger = logger;
        Interceptor = new InvalidationInterceptor(this);
    }

    public Type ContextType { get; }

    public EntityFrameworkCoreOptions Options { get; }

    public InvalidationInterceptor Interceptor { get; }

    /// <summary>The model's tag map, built and checked once per model.</summary>
    /// <exception cref="InvalidOperationException">A tag doesn't fit the model.</exception>
    public EntityTagMap GetMap(IModel model)
    {
        var built = _maps.GetValue(model, Build);
        return built.Map ?? throw new InvalidOperationException(built.Error);
    }

    /// <summary>The model's tag map, or <see langword="false"/> when a tag doesn't fit the model.</summary>
    public bool TryGetMap(IModel model, [MaybeNullWhen(false)] out EntityTagMap map)
    {
        map = _maps.GetValue(model, Build).Map;
        return map != null;
    }

    private BuiltMap Build(IModel model)
    {
        try
        {
            return new BuiltMap(EntityTagMap.Build(model, ContextType, Options), null);
        }
        catch (InvalidOperationException error)
        {
            return new BuiltMap(null, error.Message);
        }
    }

    /// <summary>Invalidates after a commit. Never throws: the data is saved, and a failure is logged and retried by the cache.</summary>
    public async ValueTask InvalidateAsync(string[] tags)
    {
        try
        {
            // Not the caller's token: the data has changed, so the invalidation must happen even if the caller has gone
            await _cache.InvalidateTagsAsync(tags, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogFailure(error, tags);
        }
    }

    /// <summary>
    /// Invalidates from a synchronous commit without blocking on the network: the memory cache is invalidated before
    /// this returns, and the distributed cache confirms in the background.
    /// </summary>
    public void Invalidate(string[] tags)
    {
        ValueTask<InvalidationResult> pending;
        try
        {
            pending = _cache.InvalidateTagsAsync(tags, CancellationToken.None);
        }
        catch (Exception error)
        {
            LogFailure(error, tags);
            return;
        }
        if (pending.IsCompletedSuccessfully) return;
        _ = ObserveAsync(pending, tags);
    }

    private async Task ObserveAsync(ValueTask<InvalidationResult> pending, string[] tags)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            LogFailure(error, tags);
        }
    }

    private sealed record BuiltMap(EntityTagMap? Map, string? Error);

    private void LogFailure(Exception error, string[] tags)
        => _logger.LogWarning(error, "MethodCache could not confirm the invalidation of {Tags} after {Context} committed.",
            string.Join(", ", tags), ContextType.Name);
}

/// <summary>The registration of <typeparamref name="TContext"/>, one per container.</summary>
internal sealed class EntityInvalidation<TContext>(EntityFrameworkCoreOptions options, ICacheManager cache, ILogger<EntityInvalidation<TContext>> logger)
    : EntityInvalidation(typeof(TContext), options, cache, logger)
    where TContext : DbContext;
