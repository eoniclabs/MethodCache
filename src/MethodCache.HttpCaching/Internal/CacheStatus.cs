using System.Globalization;
using System.Net;
using System.Text;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// This cache's member of the RFC 9211 <c>Cache-Status</c> response header, added when
/// <see cref="HttpCacheOptions.AddDiagnosticHeaders"/> is on: <c>MethodCache; hit; ttl=42</c>,
/// <c>MethodCache; fwd=uri-miss; fwd-status=200; stored</c> and so on. <c>default</c> is a hit.
/// </summary>
/// <param name="Forward">Why the request went to the origin (one of the <c>fwd</c> values), or <c>null</c> for a hit.</param>
/// <param name="ForwardStatus">The status the origin answered the forwarded request with, when it answered.</param>
/// <param name="Stored">Whether the cache stored the origin's response.</param>
/// <param name="Detail">What the other parameters cannot say, such as which stale rule served the response.</param>
internal readonly record struct CacheStatus(string? Forward = null, HttpStatusCode? ForwardStatus = null, bool Stored = false, string? Detail = null)
{
    internal const string HeaderName = "Cache-Status";
    internal const string CacheName = "MethodCache";

    // The fwd values of RFC 9211 section 2.2 the cache uses
    /// <summary>The cache does not handle this request (a range, a body, the caller's own validators, no-store).</summary>
    internal const string Bypass = "bypass";
    /// <summary>The method is not one the cache serves (only GET and HEAD are).</summary>
    internal const string Method = "method";
    /// <summary>Nothing is stored for the URL.</summary>
    internal const string UriMiss = "uri-miss";
    /// <summary>A response is stored for the URL, but for other values of its <c>Vary</c> headers.</summary>
    internal const string VaryMiss = "vary-miss";
    /// <summary>A stored response is fresh, but the request's own directives (no-cache, max-age, min-fresh) refuse it.</summary>
    internal const string Request = "request";
    /// <summary>The stored response is stale or must be validated first.</summary>
    internal const string Stale = "stale";

    internal static CacheStatus StaleWhileRevalidate => new(Detail: "stale-while-revalidate");

    /// <summary>
    /// Appends this cache's member after those already present. A response that passed through other caches (a CDN,
    /// a gateway) carries their members first; the last member is the cache closest to the client.
    /// </summary>
    /// <param name="response">The response the caller receives.</param>
    /// <param name="ttl">The stored response's remaining freshness, negative once stale, when the cache knows it.</param>
    internal void AppendTo(HttpResponseMessage response, TimeSpan? ttl = null)
        => response.Headers.TryAddWithoutValidation(HeaderName, Format(ttl));

    internal string Format(TimeSpan? ttl)
    {
        var member = new StringBuilder(CacheName, 64);
        if (Forward == null) member.Append("; hit");
        else member.Append("; fwd=").Append(Forward);
        if (ForwardStatus is { } status) member.Append("; fwd-status=").Append(((int)status).ToString(CultureInfo.InvariantCulture));
        if (Stored) member.Append("; stored");
        if (ttl is { } remaining) member.Append("; ttl=").Append(Seconds(remaining).ToString(CultureInfo.InvariantCulture));
        if (Detail != null) member.Append("; detail=").Append(Detail);
        return member.ToString();
    }

    // Whole seconds, rounded down like Age. A TimeSpan holds at most 12 digits of seconds, inside the 15 a
    // structured-field integer allows.
    private static long Seconds(TimeSpan value) => (long)Math.Floor(value.TotalSeconds);
}
