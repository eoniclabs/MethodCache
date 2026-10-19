namespace MethodCache;

/// <summary>Immutable public description of one local method override.</summary>
public sealed record RuntimeOverride
{
    /// <summary>The declared method identifier.</summary>
    public required string MethodId { get; init; }
    /// <summary>The explicitly configured settings.</summary>
    public required CacheEntryOptions Options { get; init; }
    /// <summary>The most recent update time.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Audit metadata accompanying the override.</summary>
    public RuntimeOverrideMetadata? Metadata { get; init; }
}
