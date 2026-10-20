namespace MethodCache;

/// <summary>
/// Invalidations applied in this instance's memory whose distributed delivery is still pending, counted per stage with
/// the same names as <see cref="InvalidationResult.Queued"/> and <see cref="InvalidationResult.PublicationQueued"/>.
/// The cache retries them in the background; the queues are bounded and coalesce targets when they fill.
/// </summary>
public sealed record InvalidationQueues
{
    /// <summary>Creates empty queue counts, for example in an <see cref="ICacheAdministrator"/> of your own.</summary>
    public InvalidationQueues()
    {
    }

    internal InvalidationQueues(int queued, int publicationQueued)
    {
        Queued = queued;
        PublicationQueued = publicationQueued;
    }

    /// <summary>Targets waiting for the distributed cache to confirm their invalidation.</summary>
    public int Queued { get; init; }
    /// <summary>Targets the distributed cache confirmed, waiting for the backplane to publish them to other instances.</summary>
    public int PublicationQueued { get; init; }
}
