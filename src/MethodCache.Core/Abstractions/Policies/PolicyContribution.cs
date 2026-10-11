using System;
using System.Collections.Generic;

namespace MethodCache.Policies;

public sealed record PolicyContribution(
    string SourceId,
    CachePolicyFields Fields,
    PolicyContributionKind Kind,
    DateTimeOffset AppliedAt,
    IReadOnlyDictionary<string, string?>? Metadata = null,
    string? Notes = null);
