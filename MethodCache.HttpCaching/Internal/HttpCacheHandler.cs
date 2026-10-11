using System.Diagnostics;
using System.Globalization;
using System.Net;
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
    }

    private enum Outcome
    {
        Hit,
        Miss,
        Stale,
        Revalidated,
        Bypass
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.Get(_state.Name);
        var startedAt = _metrics != null ? Stopwatch.GetTimestamp() : 0;
        var requestDirectives = RequestCacheDirectives.Parse(request);

        if (!options.CacheableMethods.Contains(request.Method) || requestDirectives.NoCache || requestDirectives.NoStore)
        {
            var bypassResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (IsUnsafeMethod(request.Method) && (int)bypassResponse.StatusCode is >= 200 and < 400)
            {
                await InvalidateAfterUnsafeRequestAsync(request, bypassResponse, options, cancellationToken).ConfigureAwait(false);
            }

            return Record(Outcome.Bypass, request, bypassResponse, startedAt);
        }

        var freshness = new FreshnessCalculator(options, _timeProvider);
        var cacheKey = HttpCacheKeys.ForRequest(_state.Name, request, options);
        var cachedEntry = await GetCacheEntryAsync(request, cacheKey, cancellationToken).ConfigureAwait(false);

        if (cachedEntry != null && freshness.GetFreshnessLifetime(cachedEntry) is { } freshnessLifetime)
        {
            var currentAge = freshness.GetCurrentAge(cachedEntry);

            if (cachedEntry.CacheControl?.NoCache != true &&
                requestDirectives.Accepts(currentAge, freshnessLifetime, freshness.MayServeStale(cachedEntry)))
            {
                return Record(Outcome.Hit, request, CreateCachedResponse(request, cachedEntry, "HIT", options, freshness), startedAt);
            }

            if (currentAge > freshnessLifetime && freshness.CanUseStaleWhileRevalidate(cachedEntry))
            {
                if (_state.RevalidationsInFlight.TryAdd(cacheKey, 0))
                {
                    _ = RevalidateInBackgroundAsync(request, cachedEntry, cacheKey, options);
                }

                return Record(Outcome.Stale, request, CreateCachedResponse(request, cachedEntry, "STALE", options, freshness), startedAt);
            }
        }

        // RFC 9111 section 5.2.1.7: only-if-cached never goes to the origin
        if (requestDirectives.OnlyIfCached)
        {
            var timeout = new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                RequestMessage = request,
                ReasonPhrase = "Cache entry not available"
            };
            return Record(Outcome.Bypass, request, timeout, startedAt);
        }

        if (cachedEntry != null)
        {
            AddConditionalHeaders(request, cachedEntry);
        }

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && cachedEntry != null && freshness.CanUseStaleIfError(cachedEntry))
        {
            _logger.LogWarning(ex, "Request failed, serving stale content for {Uri}", request.RequestUri);
            return Record(Outcome.Stale, request, CreateCachedResponse(request, cachedEntry, "STALE-IF-ERROR", options, freshness), startedAt);
        }

        if (cachedEntry != null && (int)upstreamResponse.StatusCode >= 500 && freshness.CanUseStaleIfError(cachedEntry))
        {
            _logger.LogWarning("Origin returned {StatusCode}, serving stale content for {Uri}", (int)upstreamResponse.StatusCode, request.RequestUri);
            upstreamResponse.Dispose();
            return Record(Outcome.Stale, request, CreateCachedResponse(request, cachedEntry, "STALE-IF-ERROR", options, freshness), startedAt);
        }

        if (upstreamResponse.StatusCode == HttpStatusCode.NotModified && cachedEntry != null)
        {
            _logger.LogDebug("Received 304 Not Modified for {Uri}, updating cache", request.RequestUri);
            var updatedEntry = cachedEntry.WithUpdatedHeaders(upstreamResponse, _timeProvider.GetUtcNow());
            await StoreAsync(request, cacheKey, updatedEntry, options, freshness, cancellationToken).ConfigureAwait(false);

            upstreamResponse.Dispose();
            return Record(Outcome.Revalidated, request, CreateCachedResponse(request, updatedEntry, "REVALIDATED", options, freshness), startedAt);
        }

        await TryStoreResponseAsync(request, upstreamResponse, cacheKey, options, freshness, cancellationToken).ConfigureAwait(false);

        if (options.AddDiagnosticHeaders && !upstreamResponse.Headers.Contains("X-Cache"))
        {
            upstreamResponse.Headers.TryAddWithoutValidation("X-Cache", "MISS");
        }

        return Record(Outcome.Miss, request, upstreamResponse, startedAt);
    }

    // RFC 9110 section 9.2.1: GET, HEAD, OPTIONS and TRACE are safe; everything else may change the resource.
    private static bool IsUnsafeMethod(HttpMethod method)
        => method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options && method != HttpMethod.Trace;

    /// <summary>
    /// RFC 9111 section 4.4: a successful unsafe request invalidates the cached responses for its target URI and for
    /// same-origin <c>Location</c> and <c>Content-Location</c> URIs.
    /// </summary>
    private async ValueTask InvalidateAfterUnsafeRequestAsync(HttpRequestMessage request, HttpResponseMessage response, HttpCacheOptions options, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } target)
        {
            return;
        }

        var uris = new List<Uri>(3) { target };
        AddSameOrigin(uris, target, response.Headers.Location);
        AddSameOrigin(uris, target, response.Content?.Headers.ContentLocation);

        foreach (var uri in uris)
        {
            foreach (var method in options.CacheableMethods)
            {
                using var probe = new HttpRequestMessage(method, uri);
                foreach (var header in request.Headers)
                {
                    probe.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                await RemoveWithVariantsAsync(HttpCacheKeys.ForRequest(_state.Name, probe, options), cancellationToken).ConfigureAwait(false);
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

    private async ValueTask RemoveWithVariantsAsync(string baseKey, CancellationToken cancellationToken)
    {
        var existing = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);
        if (existing == null)
        {
            return;
        }

        await RemoveKeysAsync(existing.VariantKeys, cancellationToken).ConfigureAwait(false);
        await _storage.RemoveAsync(baseKey, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RemoveKeysAsync(IEnumerable<string>? keys, CancellationToken cancellationToken)
    {
        if (keys == null)
        {
            return;
        }

        foreach (var key in keys)
        {
            await _storage.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// RFC 9111 section 4.1: a stored response with <c>Vary</c> is only used for a request whose varying headers match.
    /// </summary>
    private async ValueTask<HttpCacheEntry?> GetCacheEntryAsync(HttpRequestMessage request, string baseKey, CancellationToken cancellationToken)
    {
        var entry = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);
        if (entry?.VaryHeaders is not { Length: > 0 } varyHeaders)
        {
            return entry;
        }

        var variantKey = HttpCacheKeys.ForVariant(baseKey, request, varyHeaders);
        return variantKey == null ? null : await _storage.GetAsync(variantKey, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask TryStoreResponseAsync(
        HttpRequestMessage request,
        HttpResponseMessage response,
        string cacheKey,
        HttpCacheOptions options,
        FreshnessCalculator freshness,
        CancellationToken cancellationToken)
    {
        if (!IsStorable(request, response, options))
        {
            return;
        }

        try
        {
            var entry = await HttpCacheEntry.FromResponseAsync(request, response, _timeProvider.GetUtcNow()).ConfigureAwait(false);
            if (options.MaxResponseSize > 0 && entry.Content.Length > options.MaxResponseSize)
            {
                return;
            }

            await StoreAsync(request, cacheKey, entry, options, freshness, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Cached response for {Uri}, size: {Size} bytes", request.RequestUri, entry.Content.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to cache response for {Uri}", request.RequestUri);
        }
    }

    private async ValueTask StoreAsync(
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
            return;
        }

        var previous = await _storage.GetAsync(baseKey, cancellationToken).ConfigureAwait(false);

        if (entry.VaryHeaders is not { Length: > 0 } varyHeaders)
        {
            await RemoveKeysAsync(previous?.VariantKeys, cancellationToken).ConfigureAwait(false);
            await _storage.SetAsync(baseKey, entry with { VariantKeys = null }, timeToLive, cancellationToken).ConfigureAwait(false);
            return;
        }

        var variantKey = HttpCacheKeys.ForVariant(baseKey, request, varyHeaders);
        if (variantKey == null)
        {
            return;
        }

        // The base entry keeps the variant list, oldest first, so it lives and dies with the cached URL
        var maxVariants = Math.Max(1, options.MaxVariantsPerUrl);
        var variants = previous?.VariantKeys?.Where(k => !string.Equals(k, variantKey, StringComparison.Ordinal)).ToList() ?? new List<string>();
        variants.Add(variantKey);

        if (variants.Count > maxVariants)
        {
            var evicted = variants.GetRange(0, variants.Count - maxVariants);
            variants.RemoveRange(0, evicted.Count);
            await RemoveKeysAsync(evicted, cancellationToken).ConfigureAwait(false);
        }

        await _storage.SetAsync(variantKey, entry with { VariantKeys = null }, timeToLive, cancellationToken).ConfigureAwait(false);
        await _storage.SetAsync(baseKey, entry with { VariantKeys = variants.ToArray() }, timeToLive, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps an entry for its remaining freshness, plus <see cref="HttpCacheOptions.StaleRetention"/> when it can still
    /// be used stale: revalidated with its validators or served by stale-while-revalidate / stale-if-error.
    /// </summary>
    private static TimeSpan GetTimeToLive(HttpCacheEntry entry, HttpCacheOptions options, FreshnessCalculator freshness)
    {
        var remaining = (freshness.GetFreshnessLifetime(entry) ?? TimeSpan.Zero) - freshness.GetCurrentAge(entry);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        var usableWhenStale = entry.CanValidate() || options.EnableStaleWhileRevalidate || options.EnableStaleIfError;
        return usableWhenStale ? remaining + options.StaleRetention : remaining;
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

        if (!response.IsSuccessStatusCode &&
            response.StatusCode != HttpStatusCode.MovedPermanently &&
            response.StatusCode != HttpStatusCode.NotFound)
        {
            return false;
        }

        var cacheControl = response.Headers.CacheControl;
        if (cacheControl?.NoStore == true || request.Headers.CacheControl?.NoStore == true)
        {
            return false;
        }

        if (options.IsSharedCache && (cacheControl?.Private == true || (request.Headers.Authorization != null && !AllowsSharedAuthorized(cacheControl))))
        {
            return false;
        }

        var hasFreshness = cacheControl?.MaxAge != null ||
                           (options.IsSharedCache && cacheControl?.SharedMaxAge != null) ||
                           response.Content?.Headers.Expires != null ||
                           (options.AllowHeuristicFreshness && response.Content?.Headers.LastModified != null);
        var hasValidator = response.Headers.ETag != null || response.Content?.Headers.LastModified != null;

        return hasFreshness || hasValidator;
    }

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

    private Task RevalidateInBackgroundAsync(HttpRequestMessage originalRequest, HttpCacheEntry cachedEntry, string cacheKey, HttpCacheOptions options)
    {
        // Copy the request now: the caller may dispose the original as soon as it has the stale response
        var request = new HttpRequestMessage(originalRequest.Method, originalRequest.RequestUri);
        foreach (var header in originalRequest.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        AddConditionalHeaders(request, cachedEntry);

        return Task.Run(async () =>
        {
            using var timeout = options.BackgroundRevalidationTimeout > TimeSpan.Zero
                ? new CancellationTokenSource(options.BackgroundRevalidationTimeout)
                : new CancellationTokenSource();

            try
            {
                using var response = await base.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var freshness = new FreshnessCalculator(options, _timeProvider);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    var updatedEntry = cachedEntry.WithUpdatedHeaders(response, _timeProvider.GetUtcNow());
                    await StoreAsync(request, cacheKey, updatedEntry, options, freshness, timeout.Token).ConfigureAwait(false);
                }
                else
                {
                    await TryStoreResponseAsync(request, response, cacheKey, options, freshness, timeout.Token).ConfigureAwait(false);
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
                _state.RevalidationsInFlight.TryRemove(cacheKey, out _);
            }
        }, CancellationToken.None);
    }

    private HttpResponseMessage Record(Outcome outcome, HttpRequestMessage request, HttpResponseMessage response, long startedAt)
    {
        if (_metrics == null)
        {
            return response;
        }

        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        switch (outcome)
        {
            case Outcome.Hit:
                _metrics.RecordHit(elapsedMs);
                break;
            case Outcome.Miss:
                _metrics.RecordMiss(elapsedMs);
                break;
            case Outcome.Stale:
                _metrics.RecordStaleServed(elapsedMs);
                break;
            case Outcome.Revalidated:
                _metrics.RecordValidation(elapsedMs);
                break;
            case Outcome.Bypass:
                _metrics.RecordBypass(elapsedMs);
                break;
        }

        _metrics.RecordStatusCode((int)response.StatusCode);
        _metrics.RecordMethod(request.Method.Method);
        return response;
    }
}
