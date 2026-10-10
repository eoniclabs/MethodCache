namespace MethodCache.Core.Runtime.Memory;

/// <summary>
/// Which access-order queue a node currently lives in. Only mutated under the engine's eviction lock.
/// </summary>
internal enum QueueType : byte
{
    None = 0,
    Window = 1,
    Probation = 2,
    Protected = 3
}

/// <summary>
/// A cache entry. Nodes are immutable with respect to key and value; a write for an existing key
/// replaces the node. Expiration and bookkeeping fields are mutable.
/// </summary>
internal abstract class Node
{
    internal const int StateAlive = 0;
    internal const int StateRetired = 1; // removed from the map, policy removal pending
    internal const int StateDead = 2;    // removed from map and policy

    protected Node(string key)
    {
        Key = key;
        KeyHash = key.GetHashCode();
    }

    public string Key { get; }

    /// <summary>Cached hash of <see cref="Key"/> for the frequency sketch.</summary>
    public int KeyHash { get; }

    public abstract object? BoxedValue { get; }

    /// <summary>Time (engine nanoseconds) after which the value is no longer fresh.</summary>
    public long LogicalExpiresAt;

    /// <summary>Time (engine nanoseconds) after which the entry is physically removed. Equals <see cref="LogicalExpiresAt"/> unless fail-safe keeps stale values around.</summary>
    public long PhysicalExpiresAt;

    /// <summary>Upper bound for sliding expiration (engine nanoseconds).</summary>
    public long AbsoluteExpiresAt;

    public long CreatedAt;

    /// <summary>How long the factory took to produce this value (nanoseconds). Used by probabilistic early refresh.</summary>
    public long ComputeDuration;

    public long Weight = 1;

    public string[]? Tags;

    /// <summary>Opaque per-method policy owned by the cache manager.</summary>
    public object? Policy;

    public int State;

    /// <summary>0 = no refresh started, 1 = background refresh in progress or attempted.</summary>
    public int RefreshState;

    // Access-order queue links (guarded by the eviction lock).
    public QueueType Queue;
    public Node? Previous;
    public Node? Next;

    // Timer wheel links (guarded by the eviction lock).
    public Node? TimerPrevious;
    public Node? TimerNext;

    public bool IsAlive => Volatile.Read(ref State) == StateAlive;

    public bool IsPhysicallyExpired(long now) => now - Volatile.Read(ref PhysicalExpiresAt) >= 0;

    public bool IsLogicallyExpired(long now) => now - Volatile.Read(ref LogicalExpiresAt) >= 0;
}

/// <summary>
/// Strongly typed node; avoids boxing value types and lets readers type-check with a single type test.
/// </summary>
internal sealed class ValueNode<T> : Node
{
    public ValueNode(string key, T value)
        : base(key)
    {
        Value = value;
    }

    private Task<T>? _completedTask;

    public T Value { get; }

    public override object? BoxedValue => Value;

    /// <summary>
    /// A completed task holding <see cref="Value"/>, created on first use and reused for every later hit so
    /// asynchronous hits do not allocate. A race may create two tasks; either is correct.
    /// </summary>
    public Task<T> AsCompletedTask() => _completedTask ??= Task.FromResult(Value);
}

/// <summary>Sentinel used as the head of circular timer wheel buckets.</summary>
internal sealed class SentinelNode : Node
{
    public SentinelNode()
        : base(string.Empty)
    {
        TimerPrevious = this;
        TimerNext = this;
    }

    public override object? BoxedValue => null;
}
