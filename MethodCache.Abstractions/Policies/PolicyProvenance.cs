using System;
using System.Collections;
using System.Collections.Generic;

namespace MethodCache.Abstractions.Policies;

public sealed class PolicyProvenance : IReadOnlyCollection<PolicyContribution>
{
    private readonly PolicyContribution[] _contributions;

    public static PolicyProvenance Empty { get; } = new PolicyProvenance(Array.Empty<PolicyContribution>());

    public PolicyProvenance(IReadOnlyList<PolicyContribution> contributions)
    {
        if (contributions is null)
        {
            throw new ArgumentNullException(nameof(contributions));
        }

        _contributions = contributions as PolicyContribution[] ?? contributions.ToArray();
    }

    private PolicyProvenance(PolicyContribution[] contributions)
    {
        _contributions = contributions;
    }

    public int Count => _contributions.Length;

    public IEnumerator<PolicyContribution> GetEnumerator() => ((IEnumerable<PolicyContribution>)_contributions).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public PolicyProvenance Append(PolicyContribution contribution)
    {
        if (contribution is null)
        {
            throw new ArgumentNullException(nameof(contribution));
        }

        var next = new PolicyContribution[_contributions.Length + 1];
        Array.Copy(_contributions, next, _contributions.Length);
        next[next.Length - 1] = contribution;
        return new PolicyProvenance(next);
    }
}
