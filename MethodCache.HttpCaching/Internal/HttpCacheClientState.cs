using System.Collections.Concurrent;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// State for one named HttpClient. A singleton per client, so it survives IHttpClientFactory rotating the handlers.
/// </summary>
internal sealed class HttpCacheClientState
{
    public HttpCacheClientState(string name)
    {
        Name = name;
    }

    /// <summary>The HttpClient name; selects the named <see cref="HttpCacheOptions"/> and prefixes the cache keys.</summary>
    public string Name { get; }

    /// <summary>Cache keys with a background revalidation running, so each key is revalidated once at a time.</summary>
    public ConcurrentDictionary<string, byte> RevalidationsInFlight { get; } = new(StringComparer.Ordinal);
}
