namespace MethodCache;

/// <summary>Immutable public description of one local method override.</summary>
/// <param name="MethodId">The declared method identifier.</param>
/// <param name="Options">The explicitly configured settings.</param>
/// <param name="UpdatedAt">The most recent update time.</param>
/// <param name="Metadata">Audit metadata accompanying the override.</param>
public sealed record RuntimeOverride(string MethodId, CacheEntryOptions Options, DateTimeOffset UpdatedAt, RuntimeOverrideMetadata? Metadata);
