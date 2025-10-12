namespace MethodCache.Core.Storage.Coordination.Layers;

/// <summary>
/// Statistics for a storage layer.
/// </summary>
public sealed record LayerStats(
    string LayerId,
    long Hits,
    long Misses,
    double HitRatio,
    long Operations,
    Dictionary<string, object>? AdditionalStats = null)
{
    /// <summary>
    /// Creates empty statistics for a layer.
    /// </summary>
    public static LayerStats Empty(string layerId) => new(layerId, 0, 0, 0.0, 0, null);
}
