using MethodCache.Core.Options;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Fluent;

internal static class CachePolicyBuilderFactory
{
    /// <summary>
    /// Builds the policy for a cached method, rejecting options that only apply to <c>GetOrCreateAsync</c>.
    /// </summary>
    public static CachePolicyBuilder ForMethod(string methodId, CacheEntryOptions options)
    {
        EnsureAppliesToMethods($"cached method '{methodId}'", options);
        return FromOptions(options);
    }

    /// <summary>
    /// Builds defaults that apply to many cached methods, described by <paramref name="target"/> in errors.
    /// </summary>
    public static CachePolicyBuilder ForDefaults(string target, CacheEntryOptions options)
    {
        EnsureAppliesToMethods(target, options);
        return FromOptions(options);
    }

    public static CachePolicyBuilder FromOptions(CacheEntryOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var builder = new CachePolicyBuilder();

        if (options.Duration.HasValue)
        {
            builder.WithDuration(options.Duration.Value);
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

        return builder;
    }

    /// <summary>
    /// Callbacks, metrics and predicates only apply to a single <c>GetOrCreateAsync</c> call. Rejecting them here
    /// keeps a cached method from silently ignoring them.
    /// </summary>
    private static void EnsureAppliesToMethods(string target, CacheEntryOptions options)
    {
        string? unsupported = options switch
        {
            { OnHitCallbacks.Count: > 0 } => nameof(CacheEntryOptions.Builder.OnHit),
            { OnMissCallbacks.Count: > 0 } => nameof(CacheEntryOptions.Builder.OnMiss),
            { Metrics: not null } => nameof(CacheEntryOptions.Builder.WithMetrics),
            { Predicate: not null } => nameof(CacheEntryOptions.Builder.When),
            _ => null
        };

        if (unsupported != null)
        {
            throw new InvalidOperationException(
                $"{unsupported}() only applies to GetOrCreateAsync and cannot be configured for {target}. " +
                "Configure duration, tags, sliding expiration, refresh-ahead and version here instead.");
        }
    }
}
