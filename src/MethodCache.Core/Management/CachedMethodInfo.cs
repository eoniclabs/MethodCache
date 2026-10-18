namespace MethodCache;

/// <summary>The effective cache policy of one method.</summary>
/// <param name="MethodId">The method id, such as <c>MyApp.IUserService.GetUserAsync</c>.</param>
/// <param name="Duration">How long results are cached, or <c>null</c> for the cache default.</param>
/// <param name="Tags">Tags attached to every result of the method.</param>
/// <param name="Version">The policy version, part of the cache key when set.</param>
/// <param name="Sources">Where the policy came from, such as attributes, configuration or runtime overrides.</param>
public sealed record CachedMethodInfo(
    string MethodId,
    TimeSpan? Duration,
    IReadOnlyList<string> Tags,
    int? Version,
    IReadOnlyList<string> Sources)
{
    /// <summary>The effective immutable method options.</summary>
    public CacheEntryOptions Options { get; init; } = new CacheEntryOptions.Builder().Build();
    /// <summary>Whether the distributed namespace is shared or private to this container.</summary>
    public string L2Namespace { get; init; } = "shared";
    /// <summary>The source providing each effective setting.</summary>
    public IReadOnlyDictionary<string, string> SettingSources { get; init; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
}
