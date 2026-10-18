namespace MethodCache;

/// <summary>Pending local recovery targets, coalesced when the bounded queues fill.</summary>
/// <param name="Distributed">Targets awaiting distributed invalidation confirmation.</param>
/// <param name="Publication">Storage-confirmed targets awaiting backplane publication.</param>
public sealed record InvalidationQueues(int Distributed, int Publication);
