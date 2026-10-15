namespace MethodCache;

/// <summary>A snapshot of one cache entry. The cached value itself is not included.</summary>
/// <param name="Key">The cache key.</param>
/// <param name="ValueType">The full name of the cached value's type, or <c>null</c> when the value is null.</param>
/// <param name="Age">How long ago the value was stored.</param>
/// <param name="TimeToLive">How long until the value expires, or <c>null</c> when it does not expire.</param>
/// <param name="Tags">Tags that invalidate the entry.</param>
/// <param name="Weight">The entry's weight; 1 unless a weigher is configured.</param>
public sealed record CacheEntryInfo(
    string Key,
    string? ValueType,
    TimeSpan Age,
    TimeSpan? TimeToLive,
    IReadOnlyList<string> Tags,
    long Weight)
{
    /// <summary>Whether this entry describes a method result or an HTTP representation.</summary>
    public CacheEntryKind Kind { get; init; }
    /// <summary>Local residency or a historical distributed observation.</summary>
    public CacheEntryStorage Storage { get; init; }
    /// <summary>The configured HTTP client name, or null for a method result.</summary>
    public string? Client { get; init; }
    /// <summary>The HTTP value format, or null for a method result.</summary>
    public int? Format { get; init; }
    /// <summary>HTTP storage cost in bytes; method weight is not a byte estimate.</summary>
    public long? SizeBytes { get; init; }
    /// <summary>Explains limitations of an observation; null for a locally resident value.</summary>
    public string? Description { get; init; }
}
