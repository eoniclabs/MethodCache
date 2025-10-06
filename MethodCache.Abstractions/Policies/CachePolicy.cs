using System;
using System.Collections.Generic;

namespace MethodCache.Abstractions.Policies;

public sealed record CachePolicy
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyMetadata = new Dictionary<string, string?>(StringComparer.Ordinal);

    public static CachePolicy Empty { get; } = new CachePolicy();

    public TimeSpan? Duration { get; init; }
    public CacheLayerSettings Layers { get; init; } = CacheLayerSettings.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public CacheConsistencyMode Consistency { get; init; } = CacheConsistencyMode.Unspecified;
    public Type? KeyGeneratorType { get; init; }
    public int? Version { get; init; }
    public bool? RequireIdempotent { get; init; }
    public IReadOnlyDictionary<string, string?> Metadata { get; init; } = EmptyMetadata;
    public PolicyProvenance Provenance { get; init; } = PolicyProvenance.Empty;
}
