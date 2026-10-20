namespace MethodCache.HttpCaching;

/// <summary>
/// The HTTP origin answered, but the cache could not confirm the invalidation that answer requires. HTTP caching
/// throws it in place of the origin's response in two cases: after a successful unsafe request (POST, PUT, DELETE and
/// so on), whose cached responses must be invalidated (RFC 9111 section 4.4), and after a GET or HEAD whose answer
/// replaces a stored response that must be retired (it is no longer storable, or the URL now redirects).
/// </summary>
/// <remarks>
/// The origin's response is disposed, and the exception records nothing of the request or the response. The cache owns
/// recovery: it retries the invalidation in the background and meanwhile does not serve the affected URLs from this
/// process. Do not retry an unsafe request automatically: the origin may have applied it, and repeating it can repeat
/// its side effects. A GET or HEAD is safe to retry.
/// </remarks>
public sealed class HttpCacheInvalidationException : Exception
{
    private const string MutationMessage = "The HTTP origin mutation may have succeeded, but cache invalidation was not confirmed. Cache recovery continues independently; do not retry the origin mutation automatically.";
    private const string RetirementMessage = "The HTTP origin answered, but the cache could not confirm that it retired the stored response this answer replaces. Cache recovery continues independently; the request is safe to retry.";

    /// <summary>Creates the error for an unsafe request (a mutation), without recording request or response contents.</summary>
    public HttpCacheInvalidationException() : base(MutationMessage) { }

    private HttpCacheInvalidationException(string message) : base(message) { }

    /// <summary>The error for a GET or HEAD whose answer replaces a stored response that could not be retired.</summary>
    internal static HttpCacheInvalidationException ForRetirement() => new(RetirementMessage);
}
