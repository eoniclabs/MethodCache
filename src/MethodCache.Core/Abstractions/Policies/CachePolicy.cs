using System;
using System.Collections.Generic;

namespace MethodCache.Policies;

public sealed record CachePolicy
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyMetadata = new Dictionary<string, string?>(StringComparer.Ordinal);

    public static CachePolicy Empty { get; } = new CachePolicy();

    public TimeSpan? Duration { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public TimeSpan? SlidingExpiration { get; init; }
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
    public int? Version { get; init; }
    public IReadOnlyDictionary<string, string?> Metadata { get; init; } = EmptyMetadata;
    public PolicyProvenance Provenance { get; init; } = PolicyProvenance.Empty;
}
