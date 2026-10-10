namespace MethodCache.Core.Management;

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
    IReadOnlyList<string> Sources);
