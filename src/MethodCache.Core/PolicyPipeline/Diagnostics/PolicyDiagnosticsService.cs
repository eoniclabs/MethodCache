namespace MethodCache.Policies;

/// <summary>
/// Explains effective cache policies: what each method ends up with and which source set each field.
/// </summary>
public sealed class PolicyDiagnosticsService
{
    private readonly IPolicyRegistry _registry;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="registry">The registry to read policies from.</param>
    public PolicyDiagnosticsService(IPolicyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Returns a report for one method's effective policy.
    /// </summary>
    /// <param name="methodId">The method identifier.</param>
    /// <returns>The report.</returns>
    public PolicyDiagnosticsReport GetPolicy(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        var result = _registry.GetPolicy(methodId);
        return CreateReport(result);
    }

    /// <summary>
    /// Returns a report for every method that has a policy.
    /// </summary>
    /// <returns>One report per method.</returns>
    public IReadOnlyList<PolicyDiagnosticsReport> GetAllPolicies()
    {
        var results = _registry.GetAllPolicies();
        return results.Select(CreateReport).ToList();
    }

    /// <summary>
    /// Returns what one source contributed to a method's policy.
    /// </summary>
    /// <param name="methodId">The method identifier.</param>
    /// <param name="sourceId">The source identifier, such as <c>configuration-files</c>.</param>
    /// <returns>The source's contributions, or an empty list.</returns>
    public IReadOnlyList<PolicyContribution> GetContributions(string methodId, string sourceId)
    {
        if (sourceId == null)
        {
            throw new ArgumentNullException(nameof(sourceId));
        }

        return GetPolicy(methodId).GetContributionsFrom(sourceId);
    }

    /// <summary>
    /// Returns reports for every method the given source contributed to.
    /// </summary>
    /// <param name="sourceId">The source identifier, such as <c>runtime-overrides</c>.</param>
    /// <returns>The matching reports.</returns>
    public IReadOnlyList<PolicyDiagnosticsReport> FindBySource(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException("Source identifier is required", nameof(sourceId));
        }

        sourceId = sourceId.Trim();
        return GetAllPolicies()
            .Where(report => report.Contributions.Any(c => string.Equals(c.SourceId, sourceId, StringComparison.Ordinal)))
            .ToList();
    }

    private static PolicyDiagnosticsReport CreateReport(PolicyResolutionResult result)
    {
        var contributions = result.Contributions ?? Array.Empty<PolicyContribution>();
        var contributionsBySource = contributions
            .GroupBy(c => c.SourceId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PolicyContribution>)g.ToList(),
                StringComparer.Ordinal);

        return new PolicyDiagnosticsReport(
            result.MethodId,
            result.Policy,
            contributions,
            contributionsBySource,
            result.ResolvedAt);
    }
}

/// <summary>
/// A method's effective policy together with the contributions that built it, grouped by source.
/// </summary>
/// <param name="MethodId">The method identifier.</param>
/// <param name="Policy">The merged policy.</param>
/// <param name="Contributions">All contributions, in the order they were applied.</param>
/// <param name="ContributionsBySource">The contributions grouped by source identifier.</param>
/// <param name="ResolvedAt">When the policy was resolved.</param>
public sealed record PolicyDiagnosticsReport(
    string MethodId,
    CachePolicy Policy,
    IReadOnlyList<PolicyContribution> Contributions,
    IReadOnlyDictionary<string, IReadOnlyList<PolicyContribution>> ContributionsBySource,
    DateTimeOffset ResolvedAt)
{
    /// <summary>
    /// Returns what one source contributed to this policy.
    /// </summary>
    /// <param name="sourceId">The source identifier.</param>
    /// <returns>The source's contributions, or an empty list.</returns>
    public IReadOnlyList<PolicyContribution> GetContributionsFrom(string sourceId)
    {
        if (ContributionsBySource.TryGetValue(sourceId ?? string.Empty, out var list))
        {
            return list;
        }

        return Array.Empty<PolicyContribution>();
    }
}
