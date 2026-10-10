namespace MethodCache.Core.Management;

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
    long Weight);
