using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Abstractions.Policies;
using MethodCache.Abstractions.Registry;
using MethodCache.Abstractions.Resolution;

namespace MethodCache.Core.Configuration.Diagnostics;

public sealed class PolicyDiagnosticsService
{
    private readonly IPolicyRegistry _registry;

    public PolicyDiagnosticsService(IPolicyRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public PolicyDiagnosticsReport GetPolicy(string methodId)
    {
        if (string.IsNullOrWhiteSpace(methodId))
        {
            throw new ArgumentException("Method identifier is required", nameof(methodId));
        }

        var result = _registry.GetPolicy(methodId);
        return CreateReport(result);
    }

    public IReadOnlyList<PolicyDiagnosticsReport> GetAllPolicies()
    {
        var results = _registry.GetAllPolicies();
        return results.Select(CreateReport).ToList();
    }

    public IReadOnlyList<PolicyContribution> GetContributions(string methodId, string sourceId)
    {
        if (sourceId == null)
        {
            throw new ArgumentNullException(nameof(sourceId));
        }

        return GetPolicy(methodId).GetContributionsFrom(sourceId);
    }

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

public sealed record PolicyDiagnosticsReport(
    string MethodId,
    CachePolicy Policy,
    IReadOnlyList<PolicyContribution> Contributions,
    IReadOnlyDictionary<string, IReadOnlyList<PolicyContribution>> ContributionsBySource,
    DateTimeOffset ResolvedAt)
{
    public IReadOnlyList<PolicyContribution> GetContributionsFrom(string sourceId)
    {
        if (ContributionsBySource.TryGetValue(sourceId ?? string.Empty, out var list))
        {
            return list;
        }

        return Array.Empty<PolicyContribution>();
    }
}
