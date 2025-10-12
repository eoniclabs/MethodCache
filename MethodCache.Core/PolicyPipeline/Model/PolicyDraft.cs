using MethodCache.Abstractions.Policies;

namespace MethodCache.Core.PolicyPipeline.Model;

public readonly record struct PolicyDraft(
    string MethodId,
    CachePolicy Policy,
    CachePolicyFields Fields,
    IReadOnlyDictionary<string, string?> Metadata,
    string? Notes);
