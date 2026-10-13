using System;
using System.Collections.Generic;

namespace MethodCache.Policies;

/// <summary>
/// The caching settings for one method: how long results live, their tags, expiration, refresh and fail-safe behavior.
/// A field left unset falls through to a lower-priority source or the default.
/// </summary>
public sealed record CachePolicy
{
    private readonly IReadOnlyList<string> _tags = Array.Empty<string>();
    private static readonly IReadOnlyDictionary<string, string?> EmptyMetadata = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// A policy with no fields set.
    /// </summary>
    public static CachePolicy Empty { get; } = new CachePolicy();

    /// <summary>
    /// How long an entry lives after it is stored.
    /// </summary>
    public TimeSpan? Duration { get; init; }
    /// <summary>
    /// Tags attached to stored entries, so related entries can be invalidated together.
    /// </summary>
    public IReadOnlyList<string> Tags
    {
        get => _tags;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _tags = value.Count == 0 ? Array.Empty<string>() : Array.AsReadOnly(value.ToArray());
        }
    }
    /// <summary>
    /// How long an entry may go unread before it expires; each read resets the window.
    /// </summary>
    public TimeSpan? SlidingExpiration { get; init; }
    /// <summary>
    /// How long before an entry expires it is reloaded in the background, so callers keep getting a cached value.
    /// </summary>
    public TimeSpan? RefreshAhead { get; init; }

    /// <summary>
    /// Decides whether a freshly loaded result is stored; when it returns false the caller still gets the result,
    /// but nothing is cached. Set from code only (fluent <c>CacheWhen</c>), never from configuration files.
    /// </summary>
    public Func<object?, bool>? CacheWhen { get; init; }

    /// <summary>
    /// How long an expired value is kept and returned when reloading it fails or exceeds <see cref="FailSafeTimeout"/>.
    /// <see cref="TimeSpan.Zero"/> turns fail-safe off.
    /// </summary>
    public TimeSpan? FailSafe { get; init; }

    /// <summary>
    /// When a fail-safe value exists and a reload takes longer than this, the caller gets the fail-safe value and the
    /// reload finishes in the background.
    /// </summary>
    public TimeSpan? FailSafeTimeout { get; init; }

    /// <summary>
    /// After a fail-safe value was served, how long it is served before the method is called again. Default 30 seconds.
    /// </summary>
    public TimeSpan? FailSafeRetryAfter { get; init; }
    /// <summary>
    /// A version number that becomes part of the cache key; changing it makes previously stored entries unreachable.
    /// </summary>
    public int? Version { get; init; }
    /// <summary>
    /// Free-form key/value pairs attached to the policy, for diagnostics and custom tooling.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Metadata { get; init; } = EmptyMetadata;
    /// <summary>
    /// Which sources contributed to this policy and which fields each one set.
    /// </summary>
    internal PolicyProvenance Provenance { get; init; } = PolicyProvenance.Empty;
}
