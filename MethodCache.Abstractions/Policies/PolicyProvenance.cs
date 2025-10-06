using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace MethodCache.Abstractions.Policies;

public sealed class PolicyProvenance : IReadOnlyCollection<PolicyContribution>
{
    private readonly IReadOnlyList<PolicyContribution> _contributions;

    public static PolicyProvenance Empty { get; } = new PolicyProvenance(Array.Empty<PolicyContribution>());

    public PolicyProvenance(IReadOnlyList<PolicyContribution> contributions)
    {
        _contributions = contributions ?? throw new ArgumentNullException(nameof(contributions));
    }

    public int Count => _contributions.Count;

    public IEnumerator<PolicyContribution> GetEnumerator() => _contributions.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public PolicyProvenance Append(PolicyContribution contribution)
    {
        if (contribution is null)
        {
            throw new ArgumentNullException(nameof(contribution));
        }

        if (_contributions.Count == 0)
        {
            return new PolicyProvenance(new[] { contribution });
        }

        return new PolicyProvenance(_contributions.Concat(new[] { contribution }).ToArray());
    }
}
