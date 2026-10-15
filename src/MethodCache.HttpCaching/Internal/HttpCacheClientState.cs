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

    internal int TransportWarningLogged;
    internal int CookieWarningLogged;

    /// <summary>Cache keys with a background revalidation running, so each key is revalidated once at a time.</summary>
    public ConcurrentDictionary<string, byte> RevalidationsInFlight { get; } = new(StringComparer.Ordinal);
}

// Includes clients configured through ConfigureHttpClientDefaults, whose final name is only known by the filter.
internal sealed class HttpCacheClientStates
{
    private readonly ConcurrentDictionary<string, HttpCacheClientState> _clients = new(StringComparer.Ordinal);
    internal HttpCacheClientState For(string name) => _clients.GetOrAdd(name, static value => new HttpCacheClientState(value));
}
