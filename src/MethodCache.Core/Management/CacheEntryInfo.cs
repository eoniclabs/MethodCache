namespace MethodCache;

/// <summary>A snapshot of one cache entry. The cached value itself is not included.</summary>
public sealed record CacheEntryInfo
{
    /// <summary>
    /// The key the entry is stored under: the method's generated key, or for a manual entry the key passed to
    /// <see cref="ICacheManager"/> framed with its version. Pass it to <see cref="ICacheAdministrator.GetEntry"/> and
    /// <see cref="ICacheAdministrator.RemoveEntryAsync"/>; <see cref="ICacheManager"/> takes the key and version you
    /// stored it with instead.
    /// </summary>
    public required string StoredKey { get; init; }
    /// <summary>The full name of the cached value's type, or <c>null</c> when the value is null.</summary>
    public string? ValueType { get; init; }
    /// <summary>How long ago the value was stored.</summary>
    public TimeSpan Age { get; init; }
    /// <summary>How long until the value expires, or <c>null</c> when it does not expire.</summary>
    public TimeSpan? TimeToLive { get; init; }
    /// <summary>Tags that invalidate the entry.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
    /// <summary>The entry's weight; 1 unless a weigher is configured.</summary>
    public long Weight { get; init; }
    /// <summary>Whether this entry describes a method result or an HTTP representation.</summary>
    public CacheEntryKind Kind { get; init; }
    /// <summary>Local residency or a historical distributed observation.</summary>
    public CacheEntryStorage Storage { get; init; }
    /// <summary>The configured HTTP client name, or null for a method result.</summary>
    public string? Client { get; init; }
    /// <summary>HTTP storage cost in bytes; method weight is not a byte estimate.</summary>
    public long? SizeBytes { get; init; }
    /// <summary>Explains limitations of an observation; null for a locally resident value.</summary>
    public string? Description { get; init; }
}
