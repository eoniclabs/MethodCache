using System.Collections.Generic;
using MethodCache.Abstractions.Policies;

namespace MethodCache.Core.Configuration.Policies;

public readonly record struct PolicyDraft(
    string MethodId,
    CachePolicy Policy,
    CachePolicyFields Fields,
    IReadOnlyDictionary<string, string?> Metadata,
    string? Notes);
