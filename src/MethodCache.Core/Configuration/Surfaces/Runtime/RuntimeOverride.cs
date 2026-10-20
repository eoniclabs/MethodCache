namespace MethodCache;

/// <summary>Immutable public description of one local method override.</summary>
public sealed record RuntimeOverride
{
    /// <summary>The method id, such as <c>[MyApp]MyApp.IUserService.GetUserAsync</c>.</summary>
    public required string MethodId { get; init; }
    /// <summary>The settings the override sets; unset ones keep their values from the lower layers.</summary>
    public required CacheEntryOptions Options { get; init; }
    /// <summary>The most recent update time.</summary>
    public DateTimeOffset UpdatedAt { get; init; }
    /// <summary>Who made the override and why, and when it is removed automatically.</summary>
    public RuntimeOverrideMetadata? Metadata { get; init; }
}
