using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Internal;

/// <summary>
/// HTTP message handler that implements standards-compliant HTTP caching according to RFC 9111.
/// </summary>
internal sealed class HttpCacheHandler : DelegatingHandler
{
    private readonly HttpCacheClientState _state;
    private readonly IHttpCacheStorage _storage;
    private readonly IOptionsMonitor<HttpCacheOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly HttpCacheMetrics? _metrics;
    private readonly ILogger<HttpCacheHandler> _logger;
    private string _keyName;

    public HttpCacheHandler(
        HttpCacheClientState state,
        IHttpCacheStorage storage,
        IOptionsMonitor<HttpCacheOptions> optionsMonitor,
        TimeProvider timeProvider,
        ILogger<HttpCacheHandler> logger,
        HttpCacheMetrics? metrics = null)
    {
        _state = state;
        _storage = storage;
        _optionsMonitor = optionsMonitor;
        _timeProvider = timeProvider;
        _logger = logger;
        _metrics = metrics;
        _keyName = state.Name;
    }

    /// <summary>
    /// Caching added through <c>ConfigureHttpClientDefaults</c> has no client name when it is registered. The handler
    /// filter gives each client's handler its own name, so clients never share entries.
    /// </summary>
    internal void UseClientName(string clientName)
    {
        if (_state.Name.Length == 0)
        {
            _keyName = clientName;
        }
    }

    internal string ClientName => _keyName;

    private enum Outcome
    {
        Hit,
        Miss,
        Stale,
        Revalidated,
        Bypass,
        Error
    }

    private static readonly string[] OutcomeNames = ["hit", "miss", "stale", "revalidated", "bypass", "error"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var startedAt = _metrics != null ? Stopwatch.GetTimestamp() : 0;
        try
        {
            return await SendThroughCacheAsync(request, startedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (RecordFailure(startedAt))
        {
            throw; // never reached: the filter records the failure and lets the exception through untouched
        }
    }

    // Cookies are added below this handler and can change while a request or redirect is in flight.
    // No snapshot of the jar can reliably identify the cookies the transport actually sends.
    private bool HasUnsafeTransport(HttpCacheOptions options)
    {
        HttpMessageHandler? transport = InnerHandler;
        var hasUnverifiedHandler = false;
        while (transport is DelegatingHandler delegating)
        {
            hasUnverifiedHandler = true;
            transport = delegating.InnerHandler;
        }
        var unsafeTransport = (hasUnverifiedHandler && !options.AllowUnverifiedTransport) || transport switch
        {
            SocketsHttpHandler sockets => sockets.UseCookies,
            HttpClientHandler client => client.UseCookies,
            _ => !options.AllowUnverifiedTransport
        };
        if (unsafeTransport && Interlocked.Exchange(ref _state.TransportWarningLogged, 1) == 0)
        {
            _logger.LogWarning("HTTP caching for client {ClientName} is bypassed because its transport uses automatic cookies or cannot be verified. Disable transport cookies, or set AllowUnverifiedTransport only for a verified cookie-free custom transport.", _keyName);
        }
        return unsafeTransport;
    }

    private static HttpResponseMessage CreateCacheUnavailableResponse(HttpRequestMessage request) =>
        new(HttpStatusCode.GatewayTimeout) { RequestMessage = request, ReasonPhrase = "Cache entry not available" };

    private async Task<HttpResponseMessage> SendThroughCacheAsync(HttpRequestMessage request, long startedAt, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.Get(_state.Name);
        var requestDirectives = RequestCacheDirectives.Parse(request);

        var isUnsafe = IsUnsafeMethod(request.Method);

        // Unsafe methods are never served from cache, even if listed in CacheableMethods, so they always invalidate.
        // Range requests go to the origin: a partial response is never stored, and a stored full one isn't cut up.
        // So do requests with the caller's own validators: its 304 must reach it, not be replaced by the cache's.
        // A request with a body (a search sent as GET) is not identified by its URL alone.
        if (isUnsafe || !options.CacheableMethods.Contains(request.Method) || requestDirectives.NoStore || request.Headers.Range != null ||
            request.Content != null || HasCallerConditionals(request) || HasUnsafeTransport(options))
        {
            if (requestDirectives.OnlyIfCached)
            {
                return Record(Outcome.Bypass, request, CreateCacheUnavailableResponse(request), startedAt);
            }

            // The transport follows redirects on the same message: the URL it ends at is not the one that was changed
            var target = request.RequestUri;
            var bypassResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (isUnsafe && (int)bypassResponse.StatusCode is >= 200 and < 400)
            {
                try
                {
                    await InvalidateAfterUnsafeRequestAsync(target, request, bypassResponse, options, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    bypassResponse.Dispose();
                    throw;
                }
            }

            return Record(Outcome.Bypass, request, bypassResponse, startedAt);
        }

        var freshness = new FreshnessCalculator(options, _timeProvider);
        var urlKey = HttpCacheKeys.ForRequest(_keyName, request);
        var generation = await _storage.GetGenerationAsync(urlKey, cancellationToken).ConfigureAwait(false);
        var cacheKey = HttpCacheKeys.ForGeneration(urlKey, generation);
        var cachedEntry = await GetCacheEntryAsync(request, cacheKey, cancellationToken).ConfigureAwait(false);

        if (cachedEntry != null && freshness.GetFreshnessLifetime(cachedEntry) is { } freshnessLifetime)
        {
            var currentAge = freshness.GetCurrentAge(cachedEntry);

            // Request no-cache (RFC 9111 section 5.2.1.4): never answer from the cache, but revalidate and store
            if (cachedEntry.CacheControl?.NoCache != true && !requestDirectives.NoCache &&
                requestDirectives.Accepts(currentAge, freshnessLifetime, freshness.MayServeStale(cachedEntry)))
            {
                return Record(Outcome.Hit, request, CreateCachedResponse(request, cachedEntry, "HIT", options, freshness), startedAt);
            }

            if (currentAge > freshnessLifetime && !requestDirectives.NoCache && freshness.CanUseStaleWhileRevalidate(cachedEntry))
            {
                if (!requestDirectives.OnlyIfCached && _state.RevalidationsInFlight.TryAdd(cacheKey, 0))
                {
                    _ = RevalidateInBackgroundAsync(request, cachedEntry, urlKey, cacheKey, options);
                }

                return Record(Outcome.Stale, request, CreateCachedResponse(request, cachedEntry, "STALE", options, freshness), startedAt);
            }
        }

        // RFC 9111 section 5.2.1.7: only-if-cached never goes to the origin
        if (requestDirectives.OnlyIfCached)
        {
            var timeout = CreateCacheUnavailableResponse(request);
            return Record(Outcome.Bypass, request, timeout, startedAt);
        }

        // The transport changes the message it sends: it follows redirects (new URL and method, no Authorization) and adds
        // headers such as Accept-Encoding. Entries are stored for the request as the caller sent it.
        using var sent = CopyRequest(request);

        if (cachedEntry != null)
        {
            AddConditionalHeaders(request, cachedEntry);
        }

        HttpResponseMessage upstreamResponse;
        var requestStartedAt = _timeProvider.GetTimestamp();
        try
        {
            upstreamResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        // An HttpClient timeout surfaces as TaskCanceledException; only the caller's own cancellation skips stale-if-error
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !requestDirectives.NoCache && cachedEntry != null && freshness.CanUseStaleIfError(cachedEntry))
        {
            _logger.LogWarning(ex, "Request failed, serving stale content for {Uri}", sent.RequestUri);
            return Record(Outcome.Stale, request, CreateCachedResponse(request, cachedEntry, "STALE-IF-ERROR", options, freshness), startedAt);
        }
        finally
        {
            // The validators are the cache's own: a retry handler that sends this message again must not see them as the caller's
            if (cachedEntry != null)
            {
                RemoveConditionalHeaders(request, cachedEntry);
            }
        }

        // A followed redirect answered a different URL (or method): it says nothing about this one
        if (IsRedirected(sent, request, upstreamResponse))
        {
            if (cachedEntry != null)
            {
                if (upstreamResponse.StatusCode == HttpStatusCode.NotModified)
                {
                    // The target matched the validators the cache added for this URL; the caller asked for a full response
                    upstreamResponse.Dispose();
                    upstreamResponse = await base.SendAsync(CopyRequest(sent), cancellationToken).ConfigureAwait(false);
                }

                // The URL redirects now, so its stored response is out of date
                try
                {
                    await RemoveUrlAsync(urlKey, cacheKey, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    upstreamResponse.Dispose();
                    throw;
                }
            }

            return Record(Outcome.Bypass, request, upstreamResponse, startedAt);
        }

        try
        {
            if (cachedEntry != null && !requestDirectives.NoCache && (int)upstreamResponse.StatusCode >= 500 && freshness.CanUseStaleIfError(cachedEntry))
            {
                _logger.LogWarning("Origin returned {StatusCode}, serving stale content for {Uri}", (int)upstreamResponse.StatusCode, sent.RequestUri);
                upstreamResponse.Dispose();
                return Record(Outcome.Stale, request, CreateCachedResponse(request, cachedEntry, "STALE-IF-ERROR", options, freshness), startedAt);
            }

            if (upstreamResponse.StatusCode == HttpStatusCode.NotModified && cachedEntry != null)
            {
                _logger.LogDebug("Received 304 Not Modified for {Uri}, updating cache", sent.RequestUri);
                var updatedEntry = cachedEntry.WithUpdatedHeaders(upstreamResponse, _timeProvider.GetUtcNow(), _timeProvider.GetElapsedTime(requestStartedAt));
                await StoreRevalidatedEntryAsync(sent, urlKey, cacheKey, updatedEntry, options, freshness, cancellationToken).ConfigureAwait(false);

                var revalidated = CreateCachedResponse(request, updatedEntry, "REVALIDATED", options, freshness);

                // Not stored, but this caller's own: the origin set it on this response
                if (upstreamResponse.Headers.TryGetValues("Set-Cookie", out var setCookie))
                {
                    revalidated.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
                }

                upstreamResponse.Dispose();
                return Record(Outcome.Revalidated, request, revalidated, startedAt);
            }

            await TryStoreResponseAsync(sent, upstreamResponse, cacheKey, options, freshness, cancellationToken, _timeProvider.GetElapsedTime(requestStartedAt)).ConfigureAwait(false);

            if (options.AddDiagnosticHeaders && !upstreamResponse.Headers.Contains("X-Cache"))
            {
                upstreamResponse.Headers.TryAddWithoutValidation("X-Cache", "MISS");
            }

            return Record(Outcome.Miss, request, upstreamResponse, startedAt);
        }
        catch
        {
            upstreamResponse.Dispose();
            throw;
        }

    }

    // RFC 9110 section 9.2.1: GET, HEAD, OPTIONS and TRACE are safe; everything else may change the resource.
    private static bool IsUnsafeMethod(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options && method != HttpMethod.Trace;

    /// <summary>
    /// RFC 9111 section 4.4: a successful unsafe request invalidates the cached responses for its target URI and for
    /// same-origin <c>Location</c> and <c>Content-Location</c> URIs.
    /// </summary>
    private async ValueTask InvalidateAfterUnsafeRequestAsync(Uri? requestUri, HttpRequestMessage request, HttpResponseMessage response, HttpCacheOptions options, CancellationToken cancellationToken)
    {
        if (requestUri is not { IsAbsoluteUri: true } target)
        {
            return;
        }

        var uris = new List<Uri>(3) { target };
        AddSameOrigin(uris, target, response.Headers.Location);
        AddSameOrigin(uris, target, response.Content?.Headers.ContentLocation);

        // Rotate the publication namespace. Older requests can finish writing only to their orphaned namespace.
        foreach (var uri in uris)
        {
            foreach (var method in options.CacheableMethods)
            {
                await _storage.InvalidateAsync(HttpCacheKeys.ForUri(_keyName, method, uri), cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogDebug("Invalidated cached responses for {Uri} after {Method}", target, request.Method);
    }

    private static void AddSameOrigin(List<Uri> uris, Uri target, Uri? candidate)
    {
        if (candidate == null)
        {
            return;
        }

        var absolute = candidate.IsAbsoluteUri ? candidate : new Uri(target, candidate);
        if (Uri.Compare(absolute, target, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0 &&
            !uris.Contains(absolute))
        {
            uris.Add(absolute);
        }
    }

    /// <summary>
    /// RFC 9111 section 4.1: a stored response with <c>Vary</c> is only used for a request whose varying headers match.
    /// </summary>
    private async ValueTask<HttpCacheEntry?> GetCacheEntryAsync(HttpRequestMessage request, string baseKey, CancellationToken cancellationToken)
    {
        var entry = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);
        if (entry?.VariantGeneration is not { } generation)
        {
            // Stored in place for a request that sent none of its selecting headers: only such requests may use it
            return entry?.VaryHeaders is { Length: > 0 } inline && HttpCacheKeys.SendsAny(request, inline) ? null : entry;
        }

        var variantKey = HttpCacheKeys.ForVariant(baseKey, generation, request, entry.VaryHeaders ?? []);
        return variantKey == null ? null : await _storage.GetAsync(variantKey, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TryStoreResponseAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string cacheKey,
        HttpCacheOptions options,
        FreshnessCalculator freshness,
        CancellationToken cancellationToken,
        TimeSpan responseDelay = default)
    {
        if (!IsStorable(request, response, options))
        {
            return;
        }

        // Reading a response body is part of the HTTP request. A failed read cannot be treated as a cache miss:
        // the partially consumed response would reach the caller with missing bytes.
        var entry = await HttpCacheEntry.FromResponseAsync(request, response, _timeProvider.GetUtcNow(), options.MaxResponseSize, cancellationToken, responseDelay: responseDelay).ConfigureAwait(false);
        if (entry == null)
        {
            return; // Larger than MaxResponseSize; the caller still reads all of it
        }

        try
        {
            await StoreAsync(request, cacheKey, entry, options, freshness, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Cached response for {Uri}, size: {Size} bytes", request.RequestUri, entry.Content.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to cache response for {Uri}", request.RequestUri);
        }
    }

    private async ValueTask StoreRevalidatedEntryAsync(
        HttpRequestMessage request,
        string urlKey,
        string cacheKey,
        HttpCacheEntry entry,
        HttpCacheOptions options,
        FreshnessCalculator freshness,
        CancellationToken cancellationToken)
    {
        // Check the complete representation: a 304 can replace its storage and privacy policy.
        using var mergedResponse = entry.ToHttpResponse(request);
        if (IsStorable(request, mergedResponse, options))
        {
            // The origin answered: failing to refresh the response doesn't fail the request. The old entry
            // may carry the old freshness and Vary rules, though, so it is removed instead (and that must work).
            try
            {
                // Nothing written (no lifetime left) leaves the old entry, with its old rules: remove it below
                if (await StoreAsync(request, cacheKey, entry, options, freshness, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to update the cached response for {Uri}; removing it", request.RequestUri);
            }
        }

        await RemoveUrlAsync(urlKey, cacheKey, cancellationToken).ConfigureAwait(false);
    }

    // Remove the URL index too, so previously stored variants cannot be reused, and start a new
    // generation, so a response another request fetched earlier cannot be published afterwards.
    private async ValueTask RemoveUrlAsync(string urlKey, string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            await _storage.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The new generation alone makes the old entry unreachable: publish it anyway, then report the failure
            await _storage.InvalidateAsync(urlKey, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await _storage.InvalidateAsync(urlKey, cancellationToken).ConfigureAwait(false);
    }

    /// <returns>Whether a response was written; false when it may not be kept at all.</returns>
    private async ValueTask<bool> StoreAsync(
        HttpRequestMessage request,
        string baseKey,
        HttpCacheEntry entry,
        HttpCacheOptions options,
        FreshnessCalculator freshness,
        CancellationToken cancellationToken)
    {
        var timeToLive = GetTimeToLive(entry, options, freshness);
        if (timeToLive <= TimeSpan.Zero)
        {
            return false;
        }

        var selectingHeaders = HttpCacheKeys.SelectingHeaders(options, entry.VaryHeaders);
        if (selectingHeaders.Length == 0)
        {
            await _storage.SetAsync(baseKey, entry with { VariantGeneration = null }, timeToLive, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // A request that sends none of the selecting headers (an anonymous call in a private cache) shares its
        // variant with every such request, so the response is stored in the URL entry itself: one read per hit.
        var current = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);
        if (current?.VariantGeneration == null &&
            !selectingHeaders.Contains("*", StringComparer.Ordinal) &&
            !HttpCacheKeys.SendsAny(request, selectingHeaders))
        {
            await _storage.SetAsync(baseKey, entry with { VariantGeneration = null, VaryHeaders = selectingHeaders }, timeToLive, cancellationToken).ConfigureAwait(false);
            return true;
        }

        // Otherwise the URL entry holds no response, only the headers that select a variant and a generation that is
        // part of every variant key. Reusing the current generation keeps the other variants reachable; a new URL entry (after
        // an invalidation) starts a new generation, so variants cached before it are never served again.
        var generation = current?.VariantGeneration is { } existing && current.VaryHeaders is { } currentHeaders &&
                         currentHeaders.SequenceEqual(selectingHeaders, StringComparer.OrdinalIgnoreCase)
            ? existing
            : Guid.NewGuid().ToString("N");

        var variantKey = HttpCacheKeys.ForVariant(baseKey, generation, request, selectingHeaders);
        if (variantKey == null)
        {
            return false;
        }

        // The URL entry lives as long as its longest-lived variant, so a short-lived variant doesn't hide the others.
        // It holds no response, so its Expires records when it expires.
        var now = _timeProvider.GetUtcNow();
        var urlTimeToLive = generation == current?.VariantGeneration && current.Expires is { } urlExpires && urlExpires - now > timeToLive
            ? urlExpires - now
            : timeToLive;
        var urlEntry = new HttpCacheEntry
        {
            RequestUri = entry.RequestUri,
            Method = entry.Method,
            StoredAt = entry.StoredAt,
            Expires = now + urlTimeToLive,
            VaryHeaders = selectingHeaders,
            VariantGeneration = generation
        };

        await _storage.SetAsync(variantKey, entry with { VariantGeneration = null }, timeToLive, cancellationToken).ConfigureAwait(false);
        await _storage.SetAsync(baseKey, urlEntry, urlTimeToLive, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Keeps an entry for its remaining freshness, plus <see cref="HttpCacheOptions.StaleRetention"/> when it can still
    /// be used stale: revalidated with its validators or served by stale-while-revalidate / stale-if-error.
    /// </summary>
    private static readonly TimeSpan MaxTimeToLive = TimeSpan.FromDays(365);

    private static TimeSpan GetTimeToLive(HttpCacheEntry entry, HttpCacheOptions options, FreshnessCalculator freshness)
    {
        var remaining = (freshness.GetFreshnessLifetime(entry) ?? TimeSpan.Zero) - freshness.GetCurrentAge(entry);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var usableWhenStale = entry.CanValidate() || options.EnableStaleWhileRevalidate || options.EnableStaleIfError;
        var timeToLive = usableWhenStale ? remaining + options.StaleRetention : remaining;

        // Expires: 31 Dec 9999 means "forever"; keep the expiry representable
        return timeToLive > MaxTimeToLive ? MaxTimeToLive : timeToLive;
    }

    /// <summary>
    /// RFC 9111 section 3: whether a response may be stored at all.
    /// </summary>
    private static bool IsStorable(HttpRequestMessage request, HttpResponseMessage response, HttpCacheOptions options)
    {
        if (response.Content?.Headers.ContentLength is { } length && options.MaxResponseSize > 0 && length > options.MaxResponseSize)
        {
            return false;
        }

        var cacheControl = response.Headers.CacheControl;

        // A Cache-Control or Vary the parser rejects (e.g. "private; max-age=600") may carry private or no-store:
        // reading it as absent could share or keep what must not be, so such a response is not stored
        if (HasInvalidField(response.Headers, "Cache-Control", static value => CacheControlHeaderValue.TryParse(value, out _)) ||
            HasInvalidField(response.Headers, "Vary", IsTokenList))
        {
            return false;
        }

        // Other statuses may be stored only with explicit freshness, and never 206: it holds part of the representation
        var hasExplicitFreshness = cacheControl?.MaxAge != null ||
                                   (options.IsSharedCache && cacheControl?.SharedMaxAge != null) ||
                                   response.Content?.Headers.Expires != null;
        if (!IsCacheableStatus(response.StatusCode) &&
            !(hasExplicitFreshness && (int)response.StatusCode is >= 200 and < 300 and not 206))
        {
            return false;
        }

        if (cacheControl?.NoStore == true || request.Headers.CacheControl?.NoStore == true || response.Headers.Vary.Contains("*"))
        {
            return false;
        }

        // Contains, not Headers.Authorization: that is null for a value that doesn't parse, such as a bare API key
        if (options.IsSharedCache && (cacheControl?.Private == true || (request.Headers.Contains("Authorization") && !AllowsSharedAuthorized(cacheControl))))
        {
            return false;
        }

        var hasFreshness = hasExplicitFreshness ||
                           (options.AllowHeuristicFreshness && response.Content?.Headers.LastModified != null);
        var hasValidator = response.Headers.ETag != null || response.Content?.Headers.LastModified != null;

        return hasFreshness || hasValidator;
    }

    // Checks every field as received: the parsed collection keeps the valid fields and drops the others
    private static bool HasInvalidField(HttpResponseHeaders headers, string name, Func<string, bool> isValid)
    {
        if (!headers.NonValidated.TryGetValues(name, out var values))
        {
            return false;
        }

        foreach (var value in values)
        {
            if (!isValid(value))
            {
                return true;
            }
        }

        return false;
    }

    // RFC 9110 section 12.5.5: Vary is "*" or a list of field names
    private static bool IsTokenList(string value)
    {
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (item.Length == 0)
            {
                continue;
            }

            foreach (var c in item)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Status codes that are cacheable by default (RFC 9110 section 15.1). 206 Partial Content is left out: it holds
    /// only part of the representation and would be served to requests for all of it.
    /// </summary>
    private static bool IsCacheableStatus(HttpStatusCode status) => (int)status is 200 or 203 or 204 or 300 or 301 or 308 or 404 or 405 or 410 or 414 or 501;

    // RFC 9111 section 3.5: a shared cache stores a response to an authorized request only when it says it may.
    private static bool AllowsSharedAuthorized(System.Net.Http.Headers.CacheControlHeaderValue? cacheControl)
        => cacheControl != null && (cacheControl.Public || cacheControl.MustRevalidate || cacheControl.SharedMaxAge != null);

    private static HttpResponseMessage CreateCachedResponse(HttpRequestMessage request, HttpCacheEntry entry, string cacheStatus, HttpCacheOptions options, FreshnessCalculator freshness)
    {
        var response = entry.ToHttpResponse(request);
        var age = freshness.GetCurrentAge(entry);

        // RFC 9111 section 5.1: a response served from cache carries its current age
        response.Headers.Age = TimeSpan.FromSeconds(Math.Floor(age.TotalSeconds));

        if (options.AddDiagnosticHeaders)
        {
            response.Headers.Remove("X-Cache");
            response.Headers.TryAddWithoutValidation("X-Cache", cacheStatus);
            response.Headers.TryAddWithoutValidation("X-Cache-Age", age.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));

            if (freshness.GetFreshnessLifetime(entry) is { } freshnessLifetime)
            {
                var ttl = freshnessLifetime - age;
                response.Headers.TryAddWithoutValidation("X-Cache-TTL", Math.Max(0, ttl.TotalSeconds).ToString("0", CultureInfo.InvariantCulture));
            }
        }

        return response;
    }

    private static bool HasCallerConditionals(HttpRequestMessage request)
        => request.Headers.IfNoneMatch.Count > 0 || request.Headers.IfModifiedSince != null ||
           request.Headers.IfMatch.Count > 0 || request.Headers.IfUnmodifiedSince != null;

    private static void RemoveConditionalHeaders(HttpRequestMessage request, HttpCacheEntry cachedEntry)
    {
        if (!string.IsNullOrEmpty(cachedEntry.ETag))
        {
            request.Headers.IfNoneMatch.Clear();
        }

        if (cachedEntry.LastModified.HasValue)
        {
            request.Headers.IfModifiedSince = null;
        }
    }

    // SocketsHttpHandler rewrites the message it follows a redirect with; other handlers may send a new one,
    // which the response names
    private static bool IsRedirected(HttpRequestMessage sent, HttpRequestMessage request, HttpResponseMessage response)
        => Differs(sent, request) || (response.RequestMessage is { } final && Differs(sent, final));

    private static bool Differs(HttpRequestMessage sent, HttpRequestMessage request)
        => request.Method != sent.Method || request.RequestUri != sent.RequestUri;

    /// <summary>Copies a request's method, URL, version, options and headers; never its content.</summary>
    private static HttpRequestMessage CopyRequest(HttpRequestMessage source)
    {
        var copy = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };
        foreach (var option in source.Options)
        {
            copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }
        foreach (var header in source.Headers.NonValidated)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }

    private static void AddConditionalHeaders(HttpRequestMessage request, HttpCacheEntry cachedEntry)
    {
        if (!string.IsNullOrEmpty(cachedEntry.ETag))
        {
            request.Headers.IfNoneMatch.Clear();
            request.Headers.IfNoneMatch.ParseAdd(cachedEntry.ETag);
        }

        if (cachedEntry.LastModified.HasValue)
        {
            request.Headers.IfModifiedSince = cachedEntry.LastModified.Value;
        }
    }

    private Task RevalidateInBackgroundAsync(HttpRequestMessage originalRequest, HttpCacheEntry cachedEntry, string urlKey, string cacheKey, HttpCacheOptions options)
    {
        // Copy the request now: the caller may dispose the original as soon as it has the stale response.
        // One copy is sent (the transport changes it), the other keys what is stored.
        var request = CopyRequest(originalRequest);
        var sent = CopyRequest(originalRequest);

        AddConditionalHeaders(request, cachedEntry);

        return Task.Run(async () =>
        {
            using var timeout = options.BackgroundRevalidationTimeout > TimeSpan.Zero
                ? new CancellationTokenSource(options.BackgroundRevalidationTimeout, _timeProvider)
                : new CancellationTokenSource();

            try
            {
                var requestStartedAt = _timeProvider.GetTimestamp();
                using var response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var freshness = new FreshnessCalculator(options, _timeProvider);

                if (IsRedirected(sent, request, response))
                {
                    // Answered by another URL: the stored response for this one is out of date
                    await RemoveUrlAsync(urlKey, cacheKey, timeout.Token).ConfigureAwait(false);
                }
                else if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    var updatedEntry = cachedEntry.WithUpdatedHeaders(response, _timeProvider.GetUtcNow(), _timeProvider.GetElapsedTime(requestStartedAt));
                    await StoreRevalidatedEntryAsync(sent, urlKey, cacheKey, updatedEntry, options, freshness, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    await TryStoreResponseAsync(sent, response, cacheKey, options, freshness, timeout.Token, _timeProvider.GetElapsedTime(requestStartedAt)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _logger.LogDebug("Background revalidation timed out for {Uri}", request.RequestUri);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background revalidation failed for {Uri}", request.RequestUri);
            }
            finally
            {
                request.Dispose();
                sent.Dispose();
                _state.RevalidationsInFlight.TryRemove(cacheKey, out _);
            }
        }, CancellationToken.None);
    }

    private HttpResponseMessage Record(Outcome outcome, HttpRequestMessage request, HttpResponseMessage response, long startedAt)
    {
        _metrics?.Record(_keyName, OutcomeNames[(int)outcome], Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return response;
    }

    private bool RecordFailure(long startedAt)
    {
        _metrics?.Record(_keyName, OutcomeNames[(int)Outcome.Error], Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return false;
    }
}
