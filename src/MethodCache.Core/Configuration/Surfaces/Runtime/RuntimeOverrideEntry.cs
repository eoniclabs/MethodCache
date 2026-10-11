using MethodCache.Policies;

namespace MethodCache;

/// <summary>
/// Represents a single runtime override update.
/// </summary>
public sealed record RuntimeOverrideEntry(
    string MethodId,
    CachePolicy Policy,
    CachePolicyFields Fields = CachePolicyFields.None,
    RuntimeOverrideMetadata? Metadata = null);
