using MethodCache.Policies;

namespace MethodCache.Core.PolicyPipeline.Model;

internal readonly record struct PolicyDraft(
    string MethodId,
    CachePolicy Policy,
    CachePolicyFields Fields,
    IReadOnlyDictionary<string, string?> Metadata,
    string? Notes);
