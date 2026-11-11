using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Fluent;

internal static class CachePolicyBuilderFactory
{
    /// <summary>
    /// Builds the policy for a cached method.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> sets WithName.</exception>
    public static CachePolicyBuilder ForMethod(string methodId, CacheEntryOptions options)
    {
        RejectName($"mc.Method(...) for '{methodId}'", options);
        return FromOptions(options);
    }

    // A name labels an application's own call; a cached method is always reported by its method id
    private static void RejectName(string target, CacheEntryOptions? options)
    {
        if (options?.Name != null)
        {
            throw new InvalidOperationException(
                $"WithName is set in {target}, but WithName names an application's own GetOrCreateAsync call in metrics and logs, " +
                "and a cached method is always named by its method id. Remove WithName there.");
        }
    }

    /// <summary>
    /// Builds defaults that apply to many cached methods, described by <paramref name="target"/> in errors.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> sets CacheWhen, DurationFor, SlidingFor or TagsFor.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="options"/> sets WithName, or ClearTags while <paramref name="clearsTags"/> is false: the defaults for
    /// every method have no layer under them whose tags they could clear.
    /// </exception>
    public static CachePolicyBuilder ForDefaults(string target, CacheEntryOptions options, bool clearsTags = true)
    {
        RejectName(target, options);
        if (!clearsTags && options?.ClearsTags == true)
        {
            throw new InvalidOperationException(
                $"ClearTags is set in {target}, but the defaults for every method are the lowest layer, so there are no tags under " +
                "them to clear. Set ClearTags on a group with mc.Group(...) or on one method with mc.Method(...).");
        }

        // A predicate checks one result type, so in defaults it would reject every other method's results
        if (options?.CacheWhen != null)
        {
            throw new InvalidOperationException(
                $"CacheWhen is set in {target}, but a CacheWhen predicate checks the result type of one method, so it can only be " +
                "set for one method: mc.Method(...).CacheWhen(...) or mc.Method(...).Configure(o => o.CacheWhen<T>(...)).");
        }

        if (options?.DurationFor != null)
        {
            throw new InvalidOperationException(
                $"DurationFor is set in {target}, but a DurationFor function reads the result type of one method, so it can only be " +
                "set for one method: mc.Method(...).DurationFor(...) or mc.Method(...).Configure(o => o.DurationFor<T>(...)).");
        }

        if (options?.SlidingFor != null)
        {
            throw new InvalidOperationException(
                $"SlidingFor is set in {target}, but a SlidingFor function reads the result type of one method, so it can only be " +
                "set for one method: mc.Method(...).SlidingFor(...) or mc.Method(...).Configure(o => o.SlidingFor<T>(...)).");
        }

        if (options?.TagsFor != null)
        {
            throw new InvalidOperationException(
                $"TagsFor is set in {target}, but a TagsFor function reads the result type of one method, so it can only be " +
                "set for one method: mc.Method(...).TagsFor(...) or mc.Method(...).Configure(o => o.TagsFor<T>(...)).");
        }

        return FromOptions(options!);
    }

    public static CachePolicyBuilder FromOptions(CacheEntryOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var builder = new CachePolicyBuilder();
        if (options.Enabled is { } enabled) builder.WithEnabled(enabled);
        if (options.Distributed is { } distributed) builder.WithDistributed(distributed);
        if (options.CacheWhen != null) builder.WithCacheWhen(options.CacheWhen);
        if (options.DurationFor != null) builder.WithDurationFor(options.DurationFor);
        if (options.TagsFor != null) builder.WithTagsFor(options.TagsFor);
        if (options.SlidingFor != null) builder.WithSlidingFor(options.SlidingFor);

        if (options.Duration.HasValue)
        {
            builder.WithDuration(options.Duration.Value);
        }

        if (options.ClearsTags)
        {
            builder.ClearTags();
        }

        if (options.Tags.Count > 0)
        {
            builder.SetTags(options.Tags);
        }

        if (options.SlidingExpiration.HasValue)
        {
            builder.WithSlidingExpiration(options.SlidingExpiration.Value);
        }

        if (options.RefreshAhead.HasValue)
        {
            builder.WithRefreshAhead(options.RefreshAhead.Value);
        }

        if (options.Version.HasValue)
        {
            builder.WithVersion(options.Version.Value);
        }

        if (options.FailSafe.HasValue)
        {
            builder.WithFailSafe(options.FailSafe.Value);
        }

        if (options.FailSafeTimeout.HasValue)
        {
            builder.WithFailSafeTimeout(options.FailSafeTimeout.Value);
        }

        if (options.FailSafeRetryAfter.HasValue)
        {
            builder.WithFailSafeRetryAfter(options.FailSafeRetryAfter.Value);
        }

        return builder;
    }

}
