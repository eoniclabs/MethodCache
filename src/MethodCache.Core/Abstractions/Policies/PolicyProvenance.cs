using System;
using System.Collections;
using System.Collections.Generic;

namespace MethodCache.Policies;

/// <summary>
/// The ordered, immutable list of <see cref="PolicyContribution"/> entries that built a policy.
/// </summary>
internal sealed class PolicyProvenance : IReadOnlyCollection<PolicyContribution>
{
    private readonly PolicyContribution[] _buffer;
    private readonly int _count;

    /// <summary>
    /// A provenance with no contributions.
    /// </summary>
    public static PolicyProvenance Empty { get; } = new PolicyProvenance(Array.Empty<PolicyContribution>(), 0);

    /// <summary>
    /// Creates a provenance holding a copy of the given contributions.
    /// </summary>
    /// <param name="contributions">The contributions, in the order they were applied; none may be null.</param>
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

    /// <inheritdoc />
    public int Count => _count;

    /// <inheritdoc />
    public IEnumerator<PolicyContribution> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _buffer[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Returns a new provenance with the contribution added at the end; this instance is left unchanged.
    /// </summary>
    /// <param name="contribution">The contribution to add.</param>
    /// <returns>A new provenance including <paramref name="contribution"/>.</returns>
    public PolicyProvenance Append(PolicyContribution contribution)
    {
        if (contribution is null)
        {
            throw new ArgumentNullException(nameof(contribution));
        }

        var newCount = _count + 1;
        var newBuffer = new PolicyContribution[newCount];

        if (_count > 0)
        {
            Array.Copy(_buffer, 0, newBuffer, 0, _count);
        }

        newBuffer[_count] = contribution;
        return new PolicyProvenance(newBuffer, newCount);
    }
}
