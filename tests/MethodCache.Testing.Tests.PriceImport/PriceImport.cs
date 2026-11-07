namespace MethodCache.Testing.Tests.PriceImport;

/// <summary>Imports prices in another process and invalidates the tags the API caches them under.</summary>
public sealed class PriceImport(ICacheManager cache)
{
    public ValueTask<InvalidationResult> ImportedAsync(int id, CancellationToken cancellationToken)
        => cache.InvalidateTagsAsync(["imported-prices", $"imported-price:{id}"], cancellationToken);
}
