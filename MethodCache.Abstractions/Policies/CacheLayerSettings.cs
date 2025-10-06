namespace MethodCache.Abstractions.Policies;

public sealed record CacheLayerSettings(
    bool? EnableLayer1 = null,
    bool? EnableLayer2 = null,
    bool? EnableLayer3 = null,
    string? PreferredDistributedProvider = null,
    CacheWriteStrategy WriteStrategy = CacheWriteStrategy.Unspecified)
{
    public static CacheLayerSettings Empty { get; } = new();
}
