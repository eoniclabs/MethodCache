namespace MethodCache;

/// <summary>The effective cache policy of one method.</summary>
public sealed record CachedMethodInfo
{
    /// <summary>The method id, such as <c>[MyApp]MyApp.IUserService.GetUserAsync</c>.</summary>
    public required string MethodId { get; init; }
    /// <summary>The effective immutable method options, as resolved from attributes, configuration and overrides.</summary>
    public required CacheEntryOptions Options { get; init; }
    /// <summary>The source providing each effective setting, such as an attribute, configuration or a runtime override.</summary>
    public IReadOnlyDictionary<string, string> SettingSources { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// Whether the method's distributed entries are shared with other instances that have the same configuration;
    /// <c>false</c> while a local runtime override gives this instance a private namespace.
    /// </summary>
    public bool IsDistributedNamespaceShared { get; init; } = true;
}
