using MethodCache.EntityFrameworkCore;

namespace Microsoft.EntityFrameworkCore;

/// <summary>
/// Queues cache invalidations on an Entity Framework Core context.
/// </summary>
public static class MethodCacheDbContextExtensions
{
    /// <summary>
    /// Invalidates <paramref name="tags"/> once the context's changes are committed: after the open transaction
    /// commits, or without one after the next successful <c>SaveChanges</c>.
    /// </summary>
    /// <param name="context">A context set up with <c>mc.UseEntityFrameworkCore&lt;TContext&gt;()</c>.</param>
    /// <param name="tags">
    /// The tags, exactly as cached methods write them. Build them with <c>IMethodCacheKeys.Tag</c>, or with
    /// <c>MethodCacheKeys.Part</c> around each value.
    /// </param>
    /// <remarks>
    /// A rollback drops the tags. Tags still queued when the context is disposed, because no <c>SaveChanges</c> or
    /// commit followed, are invalidated then. After a write that is already committed, such as an
    /// <c>ExecuteUpdateAsync</c> outside a transaction, call <c>ICacheManager.InvalidateTagsAsync</c> instead.
    /// </remarks>
    /// <exception cref="ArgumentException">A tag is <see langword="null"/>, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The context wasn't set up with <c>UseEntityFrameworkCore</c>.</exception>
    public static void InvalidateAfterCommit(this DbContext context, params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var tag in tags) ArgumentException.ThrowIfNullOrWhiteSpace(tag, nameof(tags));

        var state = InvalidationInterceptor.State(context);
        if (state?.Registration == null)
        {
            var name = context.GetType().Name;
            throw new InvalidOperationException(
                $"{name} is not connected to MethodCache. Add mc.UseEntityFrameworkCore<{name}>() in AddMethodCache.");
        }
        state.Add(tags);
    }
}
