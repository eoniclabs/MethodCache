using MethodCache.Abstractions.Policies;
using MethodCache.Core.PolicyPipeline.Model;

namespace MethodCache.Core.Configuration.Surfaces.Runtime;

/// <summary>
/// Represents a single runtime override update.
/// </summary>
public sealed record RuntimeOverrideEntry(
    string MethodId,
    CachePolicy Policy,
    CachePolicyFields Fields = CachePolicyFields.None,
    RuntimeOverrideMetadata? Metadata = null);
