using MethodCache.Core.Telemetry;
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
    private HttpCacheClientState _state; // Bound once by the builder filter, before this handler is published.
    private readonly IHttpCacheStorage _storage;
    private readonly IOptionsMonitor<HttpCacheOptions> _optionsMonitor;
    private readonly TimeProvider _timeProvider;
    private readonly CoreTelemetry? _metrics;
    private readonly ILogger<HttpCacheHandler> _logger;
    private string _keyName;
    // Only immutable configuration is memoized; request data and the active view stay in invocation locals.
    private StorageBinding? _binding;
    private sealed record StorageBinding(HttpCacheOptions Options, string Client, IHttpCacheStorage Store);

    private IHttpCacheStorage CaptureStorage(HttpCacheOptions options)
    {
        var binding = Volatile.Read(ref _binding);
        if (binding != null && ReferenceEquals(binding.Options, options) && binding.Client == _keyName) return binding.Store;
        var store = _storage.ForClient(_keyName, options);
        Volatile.Write(ref _binding, new StorageBinding(options, _keyName, store));
        return store;
    }

    public HttpCacheHandler(
        HttpCacheClientState state,
        IHttpCacheStorage storage,
        IOptionsMonitor<HttpCacheOptions> optionsMonitor,
        TimeProvider timeProvider,
        ILogger<HttpCacheHandler> logger,
        CoreTelemetry? metrics = null)
    {
        _state = state;
        _storage = storage;
        _optionsMonitor = optionsMonitor;
        _timeProvider = timeProvider;
        _logger = logger;
        _metrics = metrics;
        _keyName = state.Name;
        _clientIdentity = HttpCacheKeys.ClientIdentity(_keyName);
    }

    /// <summary>
    /// Caching added through <c>ConfigureHttpClientDefaults</c> has no client name when it is registered. The handler
    /// filter gives each client's handler its own name, so clients never share entries.
    /// </summary>
    internal void UseClientName(string clientName, HttpCacheClientState state)
    {
        if (_state.Name.Length == 0)
        {
            _keyName = clientName;
            _clientIdentity = HttpCacheKeys.ClientIdentity(clientName);
            _state = state;
        }
    }

    private string _clientIdentity;

    internal string ClientName => _keyName;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = SendThroughCacheAsync(request, cancellationToken);
            return response.IsCompletedSuccessfully ? Task.FromResult(response.Result) : ObserveResponseAsync(response);
        }
        catch
        {
            _metrics?.Request("http", _keyName, RequestResult.Error);
            throw;
        }
    }

    private async Task<HttpResponseMessage> ObserveResponseAsync(ValueTask<HttpResponseMessage> pending)
    {
        try { return await pending.ConfigureAwait(false); }
        catch
        {
            _metrics?.Request("http", _keyName, RequestResult.Error);
            throw;
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

    private async ValueTask<HttpResponseMessage> SendThroughCacheAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.Get(_keyName);
        var storage = CaptureStorage(options);
        var requestDirectives = RequestCacheDirectives.Parse(request);

        var isUnsafe = IsUnsafeMethod(request.Method);

        // Only GET and HEAD are served from or stored in the cache; a successful unsafe method invalidates below.
        // Range requests go to the origin: a partial response is never stored, and a stored full one isn't cut up.
        // So do requests with the caller's own validators: its 304 must reach it, not be replaced by the cache's.
        // A request with a body (a search sent as GET) is not identified by its URL alone.
        if (options.KeepCookies || !IsCacheableMethod(request.Method) || requestDirectives.NoStore || request.Headers.Range != null ||
            request.Content != null || HasCallerConditionals(request) || HasUnsafeTransport(options))
        {
            if (requestDirectives.OnlyIfCached)
            {
                return Record(RequestResult.Bypass, CreateCacheUnavailableResponse(request));
            }

            // The transport follows redirects on the same message: the URL it ends at is not the one that was changed
            var target = request.RequestUri;
            var bypassResponse = await SendOriginAsync(request, cancellationToken).ConfigureAwait(false);

            if (isUnsafe && (int)bypassResponse.StatusCode is >= 200 and < 400)
            {
                try
                {
                    await InvalidateAfterUnsafeRequestAsync(storage, target, request, bypassResponse, options, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    bypassResponse.Dispose();
                    throw;
                }
            }

            return Record(RequestResult.Bypass, bypassResponse);
        }

        var freshness = new FreshnessCalculator(options, _timeProvider);
        var urlKey = HttpCacheKeys.ForRequestWithIdentity(_clientIdentity, request);
        var generation = await storage.GetGenerationAsync(urlKey, cancellationToken).ConfigureAwait(false);
        if (generation.StartsWith("unavailable:", StringComparison.Ordinal))
        {
            return requestDirectives.OnlyIfCached
                ? Record(RequestResult.Bypass, CreateCacheUnavailableResponse(request))
                : Record(RequestResult.Bypass, await SendOriginAsync(request, cancellationToken).ConfigureAwait(false));
        }
        var cacheKey = HttpCacheKeys.ForGeneration(urlKey, generation);
        var cachedEntry = await GetCacheEntryAsync(storage, request, cacheKey, cancellationToken).ConfigureAwait(false);

        if (cachedEntry != null && freshness.GetFreshnessLifetime(cachedEntry) is { } freshnessLifetime)
        {
            var currentAge = freshness.GetCurrentAge(cachedEntry);

            // Request no-cache (RFC 9111 section 5.2.1.4): never answer from the cache, but revalidate and store
            if (cachedEntry.CacheControl?.NoCache != true && !requestDirectives.NoCache &&
                requestDirectives.Accepts(currentAge, freshnessLifetime, freshness.MayServeStale(cachedEntry)))
            {
                return Record(RequestResult.Hit, CreateCachedResponse(request, cachedEntry, "HIT", options, freshness));
            }

            if (currentAge > freshnessLifetime && requestDirectives.AcceptsStale(currentAge, freshnessLifetime) && freshness.CanUseStaleWhileRevalidate(cachedEntry))
            {
                if (!requestDirectives.OnlyIfCached && _state.RevalidationsInFlight.TryAdd(cacheKey, 0))
                {
                    _ = RevalidateInBackgroundAsync(storage, request, cachedEntry, urlKey, cacheKey, options);
                }

                return Record(RequestResult.Stale, CreateCachedResponse(request, cachedEntry, "STALE", options, freshness));
            }
        }

        // RFC 9111 section 5.2.1.7: only-if-cached never goes to the origin
        if (requestDirectives.OnlyIfCached)
        {
            var timeout = CreateCacheUnavailableResponse(request);
            return Record(RequestResult.Bypass, timeout);
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
            upstreamResponse = await SendOriginAsync(request, cancellationToken).ConfigureAwait(false);
        }
        // An HttpClient timeout surfaces as TaskCanceledException; only the caller's own cancellation skips stale-if-error
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !requestDirectives.NoCache && cachedEntry != null && CanServeStaleIfError(requestDirectives, cachedEntry, freshness))
        {
            _logger.LogWarning("HTTP origin failed; serving stale content. Error type: {ErrorType}", ex.GetType().FullName);
            return Record(RequestResult.Stale, CreateCachedResponse(request, cachedEntry, "STALE-IF-ERROR", options, freshness));
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
                    upstreamResponse = await SendOriginAsync(CopyRequest(sent), cancellationToken).ConfigureAwait(false);
                }

                // The URL redirects now, so its stored response is out of date
                try
                {
                    await RemoveUrlAsync(storage, urlKey, cacheKey, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    upstreamResponse.Dispose();
                    throw;
                }
            }

            return Record(RequestResult.Bypass, upstreamResponse);
        }

        try
        {
            if (cachedEntry != null && !requestDirectives.NoCache && (int)upstreamResponse.StatusCode >= 500 && CanServeStaleIfError(requestDirectives, cachedEntry, freshness))
            {
                _logger.LogWarning("HTTP origin returned {StatusCode}; serving stale content.", (int)upstreamResponse.StatusCode);
                upstreamResponse.Dispose();
                return Record(RequestResult.Stale, CreateCachedResponse(request, cachedEntry, "STALE-IF-ERROR", options, freshness));
            }

            if (upstreamResponse.StatusCode == HttpStatusCode.NotModified && cachedEntry != null)
            {
                _logger.LogDebug("HTTP origin returned 304; updating cache.");
                var updatedEntry = cachedEntry.WithUpdatedHeaders(upstreamResponse, _timeProvider.GetUtcNow(), _timeProvider.GetElapsedTime(requestStartedAt));
                // Validate the representation that will be returned instead of constructing and disposing
                // a second copy of its response/content/header objects. Caller-only headers are added later.
                var revalidated = updatedEntry.ToHttpResponse(request);
                try
                {
                    await StoreRevalidatedEntryAsync(storage, sent, urlKey, cacheKey, updatedEntry, options, freshness, cancellationToken, revalidated).ConfigureAwait(false);
                    AddCachedHeaders(revalidated, updatedEntry, "REVALIDATED", options, freshness);

                    // Not stored, but this caller's own: the origin set it on this response
                    if (upstreamResponse.Headers.TryGetValues("Set-Cookie", out var setCookie))
                    {
                        revalidated.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
                    }

                    upstreamResponse.Dispose();
                    return Record(RequestResult.Revalidated, revalidated);
                }
                catch
                {
                    revalidated.Dispose();
                    throw;
                }
            }

            var stored = await TryStoreResponseAsync(storage, sent, upstreamResponse, urlKey, cacheKey, options, freshness, cancellationToken, _timeProvider.GetElapsedTime(requestStartedAt)).ConfigureAwait(false);
            if (stored == StoreResult.NotStored && cachedEntry != null && (int)upstreamResponse.StatusCode < 500)
            {
                // The origin replaced the representation. Its older body and storage policy must not survive
                // a no-store/private response, a body too large to cache, or a failed replacement write.
                await RemoveUrlAsync(storage, urlKey, cacheKey, cancellationToken).ConfigureAwait(false);
            }

            if (options.AddDiagnosticHeaders && !upstreamResponse.Headers.Contains("X-Cache"))
            {
                upstreamResponse.Headers.TryAddWithoutValidation("X-Cache", "MISS");
            }

            return Record(RequestResult.Miss, upstreamResponse);
        }
        catch
        {
            upstreamResponse.Dispose();
            throw;
        }

    }

    private static bool IsCacheableMethod(HttpMethod method) => method == HttpMethod.Get || method == HttpMethod.Head;

    // RFC 9110 section 9.2.1: GET, HEAD, OPTIONS and TRACE are safe. RFC 9111 section 4.4 treats every other method,
    // including one whose safety the cache doesn't know (QUERY, say), as unsafe: it invalidates.
    private static bool IsUnsafeMethod(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options && method != HttpMethod.Trace;

    /// <summary>
    /// RFC 9111 section 4.4: a successful unsafe request invalidates the cached responses for its target URI and for
    /// same-origin <c>Location</c> and <c>Content-Location</c> URIs.
    /// </summary>
    private async ValueTask InvalidateAfterUnsafeRequestAsync(IHttpCacheStorage storage, Uri? requestUri, HttpRequestMessage request, HttpResponseMessage response, HttpCacheOptions options, CancellationToken cancellationToken)
    {
        if (requestUri is not { IsAbsoluteUri: true } target)
        {
            return;
        }

        var uris = new List<Uri>(3) { target };
        AddSameOrigin(uris, target, response.Headers.Location);
        AddSameOrigin(uris, target, response.Content?.Headers.ContentLocation);

        // Every target starts an owned advance before caller cancellation can stop waiting.
        var targets = uris.Select(uri => HttpCacheKeys.ForUri(_keyName, HttpMethod.Get, uri)).ToArray();
        try { await storage.InvalidateManyAsync(targets, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new HttpCacheInvalidationException(); }

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
    private static ValueTask<HttpCacheEntry?> GetCacheEntryAsync(IHttpCacheStorage storage, HttpRequestMessage request, string baseKey, CancellationToken cancellationToken)
    {
        var lookup = storage.GetAsync(baseKey, cancellationToken);
        return lookup.IsCompletedSuccessfully ? SelectEntry(storage, request, baseKey, lookup.Result, cancellationToken)
            : AwaitBaseEntryAsync(storage, request, baseKey, lookup, cancellationToken);
    }

    private static async ValueTask<HttpCacheEntry?> AwaitBaseEntryAsync(IHttpCacheStorage storage, HttpRequestMessage request,
        string baseKey, ValueTask<HttpCacheEntry?> lookup, CancellationToken token)
        => await SelectEntry(storage, request, baseKey, await lookup.ConfigureAwait(false), token).ConfigureAwait(false);

    private static ValueTask<HttpCacheEntry?> SelectEntry(IHttpCacheStorage storage, HttpRequestMessage request, string baseKey,
        HttpCacheEntry? entry, CancellationToken token)
    {
        if (entry?.VariantGeneration is not { } generation)
        {
            // An inline entry can only be used by callers that still send none of its selecting headers.
            return new(entry?.VaryHeaders is { Length: > 0 } inline && HttpCacheKeys.SendsAny(request, inline) ? null : entry);
        }
        var variantKey = HttpCacheKeys.ForVariant(baseKey, generation, request, entry.VaryHeaders ?? []);
        return variantKey == null ? new((HttpCacheEntry?)null) : storage.GetAsync(variantKey, token);
    }

    private async ValueTask<StoreResult> TryStoreResponseAsync(IHttpCacheStorage storage,
        HttpRequestMessage request,
        HttpResponseMessage response,
        string urlKey,
        string cacheKey,
        HttpCacheOptions options,
        FreshnessCalculator freshness,
        CancellationToken cancellationToken,
        TimeSpan responseDelay = default)
    {
        if (!IsStorable(request, response, options))
        {
            return StoreResult.NotStored;
        }

        // Reading a response body is part of the HTTP request. A failed read cannot be treated as a cache miss:
        // the partially consumed response would reach the caller with missing bytes.
        var entry = await HttpCacheEntry.FromResponseAsync(request, response, _timeProvider.GetUtcNow(), options.MaxResponseBytes, cancellationToken, responseDelay: responseDelay, storage: storage, storageKey: cacheKey).ConfigureAwait(false);
        if (entry == null)
        {
            return StoreResult.NotStored; // Larger than MaxResponseBytes; the caller still reads all of it
        }

        var attempt = new StoreAttempt();
        try
        {
            var stored = await StoreAsync(storage, request, urlKey, cacheKey, entry, options, freshness, cancellationToken, attempt).ConfigureAwait(false);
            if (stored == StoreResult.Stored) _logger.LogDebug("Cached HTTP response, size: {Size} bytes.", entry.Content.Length);
            return stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !attempt.Retired)
        {
            _logger.LogWarning("Failed to cache HTTP response. Error type: {ErrorType}", ex.GetType().FullName);
            return StoreResult.NotStored;
        }
    }

    private async ValueTask StoreRevalidatedEntryAsync(IHttpCacheStorage storage,
        HttpRequestMessage request,
        string urlKey,
        string cacheKey,
        HttpCacheEntry entry,
        HttpCacheOptions options,
        FreshnessCalculator freshness,
        CancellationToken cancellationToken,
        HttpResponseMessage? borrowedRepresentation = null)
    {
        // Check the complete representation: a 304 can replace its storage and privacy policy.
        // Background revalidation owns its temporary response; the foreground caller owns the borrowed one.
        var attempt = new StoreAttempt();
        using var ownedRepresentation = borrowedRepresentation == null ? entry.ToHttpResponse(request) : null;
        if (IsStorable(request, borrowedRepresentation ?? ownedRepresentation!, options))
        {
            // The origin answered: failing to refresh the response doesn't fail the request. The old entry
            // may carry the old freshness and Vary rules, though, so it is removed instead (and that must work).
            try
            {
                // Nothing written (no lifetime left) leaves the old entry, with its old rules: remove it below
                if (await StoreAsync(storage, request, urlKey, cacheKey, entry, options, freshness, cancellationToken, attempt).ConfigureAwait(false) != StoreResult.NotStored)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !attempt.Retired)
            {
                _logger.LogWarning("Failed to update HTTP response; removing it. Error type: {ErrorType}", ex.GetType().FullName);
            }
        }

        await RemoveUrlAsync(storage, urlKey, cacheKey, cancellationToken).ConfigureAwait(false);
    }

    // Remove the URL index too, so previously stored variants cannot be reused, and start a new
    // generation, so a response another request fetched earlier cannot be published afterwards.
    private async ValueTask RemoveUrlAsync(IHttpCacheStorage storage, string urlKey, string cacheKey, CancellationToken cancellationToken, StoreAttempt? attempt = null)
    {
        try
        {
            await storage.RemoveAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The new generation alone makes the old entry unreachable: publish it anyway, then report the failure
            await storage.InvalidateAsync(urlKey, cancellationToken).ConfigureAwait(false);
            if (attempt != null) attempt.Retired = true;
            throw;
        }

        await storage.InvalidateAsync(urlKey, cancellationToken).ConfigureAwait(false);
        if (attempt != null) attempt.Retired = true;
    }

    private sealed class StoreAttempt { public bool Retired; }

    private static bool CanServeStaleIfError(RequestCacheDirectives directives, HttpCacheEntry entry, FreshnessCalculator freshness)
        => directives.AcceptsStale(freshness.GetCurrentAge(entry), freshness.GetFreshnessLifetime(entry) ?? TimeSpan.Zero)
            && freshness.CanUseStaleIfError(entry);

    private enum StoreResult { NotStored, Stored, Retired }

    /// <returns>Whether the entry was stored, rejected, or its old schema was already retired.</returns>
    private async ValueTask<StoreResult> StoreAsync(IHttpCacheStorage storage,
        HttpRequestMessage request,
        string urlKey,
        string baseKey,
        HttpCacheEntry entry,
        HttpCacheOptions options,
        FreshnessCalculator freshness,
        CancellationToken cancellationToken, StoreAttempt attempt)
    {
        var timeToLive = GetTimeToLive(entry, options, freshness);
        if (timeToLive <= TimeSpan.Zero)
        {
            return StoreResult.NotStored;
        }

        var selectingHeaders = HttpCacheKeys.SelectingHeaders(options, entry.VaryHeaders);
        var current = await storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);
        if (current != null && !(current.VaryHeaders ?? []).SequenceEqual(selectingHeaders, StringComparer.OrdinalIgnoreCase))
        {
            // Retire the entire old schema, including concurrent writers. This response was fetched in that
            // epoch, so it cannot move into the new one; the next request fetches and stores the new schema.
            await RemoveUrlAsync(storage, urlKey, baseKey, cancellationToken, attempt).ConfigureAwait(false);
            return StoreResult.Retired;
        }
        if (selectingHeaders.Length == 0)
        {
            return await storage.TrySetAsync(baseKey, entry with { VariantGeneration = null }, timeToLive, cancellationToken).ConfigureAwait(false) ? StoreResult.Stored : StoreResult.NotStored;
        }

        // A request that sends none of the selecting headers (an anonymous call in a private cache) shares its
        // variant with every such request, so the response is stored in the URL entry itself: one read per hit.
        if (current?.VariantGeneration == null &&
            !selectingHeaders.Contains("*", StringComparer.Ordinal) &&
            !HttpCacheKeys.SendsAny(request, selectingHeaders))
        {
            return await storage.TrySetAsync(baseKey, entry with { VariantGeneration = null, VaryHeaders = selectingHeaders }, timeToLive, cancellationToken).ConfigureAwait(false) ? StoreResult.Stored : StoreResult.NotStored;
        }

        // A deterministic schema inside the non-reusable URL epoch lets concurrent first writers, including
        // different processes, retain every variant. Changes to the schema retire the URL epoch above.
        var generation = current?.VariantGeneration ?? HttpCacheKeys.ForSelectingHeaders(selectingHeaders);

        var variantKey = HttpCacheKeys.ForVariant(baseKey, generation, request, selectingHeaders);
        if (variantKey == null)
        {
            return StoreResult.NotStored;
        }

        // The index holds no body. Keep it for the maximum supported variant lifetime so a concurrent short-lived
        // writer cannot shorten it and hide a longer-lived variant; bodies retain their own exact expiry.
        var now = _timeProvider.GetUtcNow();
        var urlTimeToLive = MaxTimeToLive;
        var urlEntry = new HttpCacheEntry
        {
            RequestUri = entry.RequestUri,
            Method = entry.Method,
            StoredAt = entry.StoredAt,
            Expires = now + urlTimeToLive,
            VaryHeaders = selectingHeaders,
            VariantGeneration = generation
        };

        if (!await storage.TrySetAsync(variantKey, entry with { VariantGeneration = null }, timeToLive, cancellationToken).ConfigureAwait(false)) return StoreResult.NotStored;
        return await storage.TrySetAsync(baseKey, urlEntry, urlTimeToLive, cancellationToken).ConfigureAwait(false) ? StoreResult.Stored : StoreResult.NotStored;
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
        var timeToLive = usableWhenStale
            ? remaining > TimeSpan.Zero && options.StaleRetention > TimeSpan.MaxValue - remaining
                ? TimeSpan.MaxValue : remaining + options.StaleRetention
            : remaining;

        // Expires: 31 Dec 9999 means "forever"; keep the expiry representable
        return timeToLive > MaxTimeToLive ? MaxTimeToLive : timeToLive;
    }

    /// <summary>
    /// RFC 9111 section 3: whether a response may be stored at all.
    /// </summary>
    private static bool IsStorable(HttpRequestMessage request, HttpResponseMessage response, HttpCacheOptions options)
    {
        if (response.Content?.Headers.ContentLength is { } length && length > options.MaxResponseBytes)
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
        => AddCachedHeaders(entry.ToHttpResponse(request), entry, cacheStatus, options, freshness);

    private static HttpResponseMessage AddCachedHeaders(HttpResponseMessage response, HttpCacheEntry entry, string cacheStatus, HttpCacheOptions options, FreshnessCalculator freshness)
    {
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

    private Task RevalidateInBackgroundAsync(IHttpCacheStorage storage, HttpRequestMessage originalRequest, HttpCacheEntry cachedEntry, string urlKey, string cacheKey, HttpCacheOptions options)
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
                using var response = await SendOriginAsync(request, timeout.Token).ConfigureAwait(false);
                var freshness = new FreshnessCalculator(options, _timeProvider);

                if (IsRedirected(sent, request, response))
                {
                    // Answered by another URL: the stored response for this one is out of date
                    await RemoveUrlAsync(storage, urlKey, cacheKey, timeout.Token).ConfigureAwait(false);
                }
                else if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    var updatedEntry = cachedEntry.WithUpdatedHeaders(response, _timeProvider.GetUtcNow(), _timeProvider.GetElapsedTime(requestStartedAt));
                    await StoreRevalidatedEntryAsync(storage, sent, urlKey, cacheKey, updatedEntry, options, freshness, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    var stored = await TryStoreResponseAsync(storage, sent, response, urlKey, cacheKey, options, freshness, timeout.Token, _timeProvider.GetElapsedTime(requestStartedAt)).ConfigureAwait(false);
                    if (stored == StoreResult.NotStored && (int)response.StatusCode < 500)
                        await RemoveUrlAsync(storage, urlKey, cacheKey, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _logger.LogDebug("HTTP background revalidation timed out.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("HTTP background revalidation failed. Error type: {ErrorType}", ex.GetType().FullName);
            }
            finally
            {
                request.Dispose();
                sent.Dispose();
                _state.RevalidationsInFlight.TryRemove(cacheKey, out _);
            }
        }, CancellationToken.None);
    }

    internal bool ShouldWarnCookies() => Interlocked.Exchange(ref _state.CookieWarningLogged, 1) == 0;

    private Task<HttpResponseMessage> SendOriginAsync(HttpRequestMessage request, CancellationToken token)
    {
        var origin = _metrics?.BeginLoad("http", _keyName);
        if (origin == null) return base.SendAsync(request, token);
        Task<HttpResponseMessage> pending;
        try { pending = base.SendAsync(request, token); }
        catch (Exception error)
        {
            origin.Fail(error);
            origin.Dispose();
            throw;
        }
        if (!pending.IsCompletedSuccessfully) return SendObservedOriginAsync(origin, pending);
        // A synchronous transport already owns the completed task. Finish its telemetry without
        // allocating an async wrapper; incomplete and faulted tasks keep the observed await path.
        origin.Dispose();
        return pending;
    }

    private static async Task<HttpResponseMessage> SendObservedOriginAsync(TelemetryOperation origin, Task<HttpResponseMessage> pending)
    {
        using (origin)
        {
            try { return await pending.ConfigureAwait(false); }
            catch (Exception error) { origin.Fail(error); throw; }
        }
    }

    private HttpResponseMessage Record(RequestResult outcome, HttpResponseMessage response)
    {
        _metrics?.Request("http", _keyName, outcome);
        return response;
    }
}
