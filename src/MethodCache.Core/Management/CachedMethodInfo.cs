namespace MethodCache;

/// <summary>The effective cache policy of one method.</summary>
public sealed record CachedMethodInfo
{
    /// <summary>The method id, such as <c>[MyApp]MyApp.IUserService.GetUserAsync</c>.</summary>
    public required string MethodId { get; init; }
    /// <summary>The effective immutable method options, as resolved from attributes, configuration and overrides.</summary>
    public required CacheEntryOptions Options { get; init; }
    /// <summary>
    /// Where each setting with an effective value comes from, keyed by its <see cref="CacheEntryOptions"/> property name,
    /// such as <c>Duration</c> or <c>FailSafeTimeout</c>. A setting no source sets is left out.
    /// </summary>
    public IReadOnlyDictionary<string, CacheSettingSource> SettingSources { get; init; } = new Dictionary<string, CacheSettingSource>();
    /// <summary>
    /// Whether the method's distributed entries are shared with other instances that have the same configuration;
    /// <c>false</c> while a local runtime override gives this instance a private namespace.
    /// </summary>
    public bool IsDistributedNamespaceShared { get; init; } = true;
}
