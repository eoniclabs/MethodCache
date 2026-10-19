namespace MethodCache;

/// <summary>Pending local recovery targets, coalesced when the bounded queues fill.</summary>
public sealed record InvalidationQueues
{
    /// <summary>Creates empty queue counts, for example in an <see cref="ICacheAdministrator"/> of your own.</summary>
    public InvalidationQueues()
    {
    }

    internal InvalidationQueues(int distributed, int publication)
    {
        Distributed = distributed;
        Publication = publication;
    }

    /// <summary>Targets awaiting distributed invalidation confirmation.</summary>
    public int Distributed { get; init; }
    /// <summary>Storage-confirmed targets awaiting backplane publication.</summary>
    public int Publication { get; init; }
}
