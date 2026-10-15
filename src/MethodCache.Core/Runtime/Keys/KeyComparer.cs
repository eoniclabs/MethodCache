using System.Runtime.CompilerServices;

namespace MethodCache.Core.Runtime.Keys;

// The mask is immutable and exists only to exercise collisions in an individual test engine.
// Production uses every output bit and a process-secret 128-bit SipHash key.
internal sealed class KeyComparer(uint mask = uint.MaxValue) : IEqualityComparer<string>
#if NET9_0_OR_GREATER
    , IAlternateEqualityComparer<ReadOnlySpan<char>, string>, IAlternateEqualityComparer<PrehashedKey, string>
#endif
{
    internal static readonly KeyComparer Instance = new();
    /// <inheritdoc />
    public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);
    /// <inheritdoc />
    public int GetHashCode(string obj) => Mask(ProcessKeyHash.Compute(obj.AsSpan()));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Mask(int hash) => unchecked((int)((uint)hash & mask));
#if NET9_0_OR_GREATER
    /// <inheritdoc />
    public bool Equals(ReadOnlySpan<char> alternate, string other) => alternate.SequenceEqual(other.AsSpan());
    /// <inheritdoc />
    public int GetHashCode(ReadOnlySpan<char> alternate) => Mask(ProcessKeyHash.Compute(alternate));
    /// <inheritdoc />
    public string Create(ReadOnlySpan<char> alternate) => new(alternate);
    /// <inheritdoc />
    public bool Equals(PrehashedKey alternate, string other) => alternate.Text.SequenceEqual(other.AsSpan());
    /// <inheritdoc />
    public int GetHashCode(PrehashedKey alternate) => Mask(alternate.Hash);
    /// <inheritdoc />
    public string Create(PrehashedKey alternate) => new(alternate.Text);
#endif
}

#if NET9_0_OR_GREATER
internal readonly ref struct PrehashedKey(ReadOnlySpan<char> text, int hash)
{
    internal ReadOnlySpan<char> Text { get; } = text;
    internal int Hash { get; } = hash;
}
#endif
