using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Fluent;

internal static class CachePolicyBuilderFactory
{
    /// <summary>
    /// Builds the policy for a cached method.
    /// </summary>
    public static CachePolicyBuilder ForMethod(string methodId, CacheEntryOptions options)
    {
        return FromOptions(options);
    }

    /// <summary>
    /// Builds defaults that apply to many cached methods, described by <paramref name="target"/> in errors.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> sets CacheWhen.</exception>
    public static CachePolicyBuilder ForDefaults(string target, CacheEntryOptions options)
    {
        // A predicate checks one result type, so in defaults it would reject every other method's results
        if (options?.CacheWhen != null)
        {
            throw new InvalidOperationException(
                $"CacheWhen is set in {target}, but a CacheWhen predicate checks the result type of one method, so it can only be " +
                "set for one method: mc.Method(...).CacheWhen(...) or mc.Method(...).Configure(o => o.CacheWhen<T>(...)).");
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
