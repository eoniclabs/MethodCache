using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;

namespace MethodCache.EntityFrameworkCore;

/// <summary>One context type's registration: its entity tags, the interceptor and the cache it invalidates.</summary>
internal class EntityInvalidation
{
    private readonly ICacheManager _cache;
    private readonly ILogger _logger;
    private readonly ConditionalWeakTable<IModel, EntityTagMap> _maps = new();

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
    public EntityTagMap GetMap(IModel model)
        => _maps.GetValue(model, m => EntityTagMap.Build(m, ContextType, Options));

    /// <summary>
    /// Records the tags of the entries about to be saved: old values now, new values after the save, when generated
    /// keys and foreign keys to new rows have their real values.
    /// </summary>
    public void Collect(DbContext context, ContextInvalidations state)
    {
        var map = GetMap(context.Model);
        if (map.IsEmpty) return;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (!map.TryGet(entry.Metadata, out var templates)) continue;
            if (entry.State != EntityState.Added) AddTags(templates, entry.OriginalValues, state.Saving);
            if (entry.State != EntityState.Deleted) state.SavedEntries.Add((entry, templates));
        }
    }

    public static void AddTags(EntityTagTemplate[] templates, PropertyValues values, HashSet<string> tags)
    {
        foreach (var template in templates) tags.Add(template.Format(values));
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

    private void LogFailure(Exception error, string[] tags)
        => _logger.LogWarning(error, "MethodCache could not confirm the invalidation of {Tags} after {Context} committed.",
            string.Join(", ", tags), ContextType.Name);
}

/// <summary>The registration of <typeparamref name="TContext"/>, one per container.</summary>
internal sealed class EntityInvalidation<TContext>(EntityFrameworkCoreOptions options, ICacheManager cache, ILogger<EntityInvalidation<TContext>> logger)
    : EntityInvalidation(typeof(TContext), options, cache, logger)
    where TContext : DbContext;
