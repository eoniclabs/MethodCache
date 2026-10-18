namespace MethodCache;

/// <summary>Counts distinct invalidation targets at each independent confirmation stage.</summary>
public sealed record InvalidationResult
{
    /// <summary>Number of distinct validated targets.</summary>
    public int Requested { get; init; }
    /// <summary>Targets invalidated locally, including targets without resident entries.</summary>
    public int Local { get; init; }
    /// <summary>Targets with invalidation metadata confirmed by distributed storage.</summary>
    public int Distributed { get; init; }
    /// <summary>Targets awaiting distributed confirmation in the recovery queue.</summary>
    public int Queued { get; init; }
    /// <summary>Targets whose backplane publication was confirmed, without receipt acknowledgements.</summary>
    public int Published { get; init; }
    /// <summary>Storage-confirmed targets awaiting backplane publication retry.</summary>
    public int PublicationQueued { get; init; }
}
