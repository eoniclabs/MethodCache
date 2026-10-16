using MethodCache.Extensibility;

namespace MethodCache.Infrastructure;

// A trusted integration may preserve Core's generated fast path while wrapping async public operations.
// Arbitrary ICacheManager decorators do not implicitly gain this capability.
internal interface IGeneratedMemoryCache
{
    InMemoryCacheManager? Memory { get; }
    T LoadSynchronously<T>(string key, string name, Func<T> factory, CacheRuntimePolicy policy, CancellationToken token);
    void InvalidateTagsSynchronously(string[] tags);
}
