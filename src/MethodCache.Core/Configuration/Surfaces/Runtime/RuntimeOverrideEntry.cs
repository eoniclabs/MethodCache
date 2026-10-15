using MethodCache.Policies;

namespace MethodCache;

/// <summary>
/// Represents a single runtime override update.
/// </summary>
internal sealed record RuntimeOverrideEntry(
    string MethodId,
    CachePolicy Policy,
    CachePolicyFields Fields = CachePolicyFields.None,
    RuntimeOverrideMetadata? Metadata = null);
