using System;
using System.Collections.Generic;

namespace MethodCache.Policies;

/// <summary>
/// Records that one policy source set some fields of a method's policy; used to explain where each setting came from.
/// </summary>
/// <param name="SourceId">The source that contributed, such as <c>attributes</c>, <c>startup-fluent</c>, <c>configuration-files</c> or <c>runtime-overrides</c>.</param>
/// <param name="Fields">The policy fields this source set.</param>
/// <param name="Kind">What the source did with those fields.</param>
/// <param name="AppliedAt">When the contribution was made.</param>
/// <param name="Metadata">Optional key/value pairs the source attached.</param>
/// <param name="Notes">An optional free-text note from the source, such as a <c>Notes</c> entry in a configuration file.</param>
public sealed record PolicyContribution(
    string SourceId,
    CachePolicyFields Fields,
    PolicyContributionKind Kind,
    DateTimeOffset AppliedAt,
    IReadOnlyDictionary<string, string?>? Metadata = null,
    string? Notes = null);
