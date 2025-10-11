using System;
using System.Collections;
using System.Collections.Generic;

namespace MethodCache.Abstractions.Policies;

public sealed class PolicyProvenance : IReadOnlyCollection<PolicyContribution>
{
    private readonly PolicyContribution[] _buffer;
    private readonly int _count;

    public static PolicyProvenance Empty { get; } = new PolicyProvenance(Array.Empty<PolicyContribution>(), 0);

    public PolicyProvenance(IReadOnlyList<PolicyContribution> contributions)
    {
        if (contributions is null)
        {
            throw new ArgumentNullException(nameof(contributions));
        }

        if (contributions.Count == 0)
        {
            _buffer = Array.Empty<PolicyContribution>();
            _count = 0;
            return;
        }

        var buffer = new PolicyContribution[contributions.Count];
        for (var i = 0; i < contributions.Count; i++)
        {
            buffer[i] = contributions[i] ?? throw new ArgumentException("Contribution collection cannot contain null entries.", nameof(contributions));
        }

        _buffer = buffer;
        _count = buffer.Length;
    }

    private PolicyProvenance(PolicyContribution[] buffer, int count)
    {
        _buffer = buffer;
        _count = count;
    }

    public int Count => _count;

    public IEnumerator<PolicyContribution> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _buffer[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public PolicyProvenance Append(PolicyContribution contribution)
    {
        if (contribution is null)
        {
            throw new ArgumentNullException(nameof(contribution));
        }

        var buffer = _buffer;
        var count = _count;

        PolicyContribution[] target;
        if (buffer.Length == count)
        {
            var newCapacity = count == 0 ? 4 : count * 2;
            target = new PolicyContribution[newCapacity];
            if (count > 0)
            {
                Array.Copy(buffer, target, count);
            }
        }
        else
        {
            target = buffer;
        }

        target[count] = contribution;
        return new PolicyProvenance(target, count + 1);
    }
}
