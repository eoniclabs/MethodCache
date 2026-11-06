using MethodCache.Infrastructure;
using MethodCache.Policies;

namespace MethodCache.Core.PolicyPipeline.Resolution;

/// <summary>
/// What the cache can honor, so a setting it can't is rejected where it is set instead of on a later call: a
/// <c>Duration</c> longer than the in-memory cache keeps entries, and a <c>CacheWhen&lt;T&gt;</c> for a type the
/// method's result can never be.
/// </summary>
internal sealed class PolicyLimits
{
    private readonly Lazy<TimeSpan?> _maxDuration;
    private readonly IReadOnlyDictionary<string, Type>? _resultTypes;

    /// <param name="maxDuration">
    /// <see cref="MethodCacheMemoryOptions.MaxExpiration"/> when the built-in cache holds the method results; null when the
    /// application's own <see cref="ICacheManager"/> does, which has its own limits. Read once, when first needed.
    /// </param>
    /// <param name="resultTypes">
    /// The result type of each cached method by id (<c>T</c> of <c>Task&lt;T&gt;</c> and <c>ValueTask&lt;T&gt;</c>);
    /// a method that isn't in it, such as a generic method, isn't checked.
    /// </param>
    public PolicyLimits(Func<TimeSpan?> maxDuration, IReadOnlyDictionary<string, Type>? resultTypes)
    {
        ArgumentNullException.ThrowIfNull(maxDuration);
        _maxDuration = new Lazy<TimeSpan?>(maxDuration, LazyThreadSafetyMode.PublicationOnly);
        _resultTypes = resultTypes;
    }

    /// <summary>Checks nothing.</summary>
    public static PolicyLimits None { get; } = new(static () => null, null);

    /// <summary>The longest <c>Duration</c> the cache keeps an entry, or <see langword="null"/> when it isn't known.</summary>
    public TimeSpan? MaxDuration => _maxDuration.Value;

    /// <summary>
    /// Why the settings <paramref name="where"/> sets for <paramref name="id"/> can't be honored, or
    /// <see langword="null"/> when they can.
    /// </summary>
    /// <param name="id">A method id, or the id of the defaults for all methods or for a group.</param>
    /// <param name="where">Where the settings are made, such as <c>mc.Method(...)</c> or <c>[Cache]</c>.</param>
    /// <param name="isMethod">Whether <paramref name="id"/> is a method's id.</param>
    /// <param name="policy">The settings.</param>
    /// <param name="fields">Which settings are made.</param>
    public string? Contradiction(string id, string where, bool isMethod, CachePolicy policy, CachePolicyFields fields)
    {
        if (fields.HasFlag(CachePolicyFields.Duration) && policy.Duration is { } duration && MaxDuration is { } max && duration > max)
        {
            var at = isMethod ? $"{where} on '{id}'" : where;
            return $"The Duration {duration} set in {at} is longer than MethodCacheMemoryOptions.MaxExpiration ({max}), the longest " +
                   "MethodCache keeps an entry, so it would be removed early. Shorten the Duration, or raise MaxExpiration with " +
                   "mc.Memory(o => o.MaxExpiration = ...).";
        }

        if (isMethod && _resultTypes != null && _resultTypes.TryGetValue(id, out var resultType))
        {
            try
            {
                if (fields.HasFlag(CachePolicyFields.CacheWhen)) CachePredicateCheck.EnsureMatches(policy.CacheWhen, resultType, id);
                if (fields.HasFlag(CachePolicyFields.DurationFor)) CachePredicateCheck.EnsureMatches(policy.DurationFor, resultType, id);
                if (fields.HasFlag(CachePolicyFields.TagsFor)) CachePredicateCheck.EnsureMatches(policy.TagsFor, resultType, id);
            }
            catch (InvalidOperationException mismatch)
            {
                return $"{mismatch.Message} It is set in {where}.";
            }
        }

        return null;
    }
}
