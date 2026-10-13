using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

/// <summary>
/// Edge cases from the third review of MethodCache.HttpCaching. Each test asserts the correct behavior.
/// The redirect and decompression tests run a real SocketsHttpHandler against a local HttpListener origin.
/// </summary>
public class HttpCacheEdgeCaseTests
{
    private const string Url = "https://api.example.com/x";

    // ---------- Redirects followed by the real transport are not stored under the original URL ----------

    [Fact]
    public async Task SendAsync_RedirectStripsAuthorization_DoesNotServeAuthorizedResponseToAnonymousCaller()
    {
        await using var origin = new LocalOrigin(context =>
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/files/42")
            {
                var auth = context.Request.Headers["Authorization"];
                if (auth == null) return LocalOrigin.Write(context, 401, "unauthorized");
                var who = auth.Replace("Bearer ", "", StringComparison.Ordinal);
                context.Response.Headers["Location"] = "/blob/" + who + "?sig=1";
                return LocalOrigin.Write(context, 302, "");
            }

            // The pre-signed blob: storable with explicit freshness, like an S3 or Azure blob with Cache-Control
            context.Response.Headers["Cache-Control"] = "max-age=300";
            context.Response.Headers["ETag"] = "\"blob\"";
            return LocalOrigin.Write(context, 200, "file of " + path["/blob/".Length..]);
        });

        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), new SocketsHttpHandler { UseCookies = false }));

        using var alice = new HttpRequestMessage(HttpMethod.Get, origin.Url("/files/42"));
        alice.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "alice");
        using var aliceResponse = await client.SendAsync(alice);
        Assert.Equal("file of alice", await aliceResponse.Content.ReadAsStringAsync());

        using var anonymous = await client.GetAsync(origin.Url("/files/42"));
        var body = await anonymous.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("unauthorized", body);
    }

    [Fact]
    public async Task SendAsync_TemporaryRedirectToLongLivedTarget_DoesNotPinTheOriginalUrl()
    {
        var latest = "v1";
        await using var origin = new LocalOrigin(context =>
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/download/latest")
            {
                context.Response.Headers["Cache-Control"] = "no-cache";
                context.Response.Headers["Location"] = "/download/" + Volatile.Read(ref latest);
                return LocalOrigin.Write(context, 302, "");
            }

            context.Response.Headers["Cache-Control"] = "max-age=31536000, immutable";
            return LocalOrigin.Write(context, 200, path["/download/".Length..]);
        });

        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), new SocketsHttpHandler { UseCookies = false }));

        Assert.Equal("v1", await client.GetStringAsync(origin.Url("/download/latest")));
        Volatile.Write(ref latest, "v2");

        Assert.Equal("v2", await client.GetStringAsync(origin.Url("/download/latest")));
    }

    [Fact]
    public async Task SendAsync_PostRedirectedToGet_InvalidatesThePostTarget()
    {
        var listVersion = 0;
        await using var origin = new LocalOrigin(context =>
        {
            var path = context.Request.Url!.AbsolutePath;
            if (path == "/items" && context.Request.HttpMethod == "POST")
            {
                Interlocked.Increment(ref listVersion);
                context.Response.Headers["Location"] = "/items/5";
                return LocalOrigin.Write(context, 303, "");
            }

            if (path == "/items")
            {
                context.Response.Headers["Cache-Control"] = "max-age=600";
                return LocalOrigin.Write(context, 200, "list-" + Volatile.Read(ref listVersion));
            }

            context.Response.Headers["Cache-Control"] = "max-age=600";
            return LocalOrigin.Write(context, 200, "item-5");
        });

        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), new SocketsHttpHandler { UseCookies = false }));

        Assert.Equal("list-0", await client.GetStringAsync(origin.Url("/items")));
        using (var post = await client.PostAsync(origin.Url("/items"), new StringContent("{}")))
        {
            Assert.Equal("item-5", await post.Content.ReadAsStringAsync());
        }

        Assert.Equal("list-1", await client.GetStringAsync(origin.Url("/items")));
    }

    // ---------- The cache's own validators are removed from the caller's request ----------

    private sealed class RetryOnceOn5xx : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode < 500) return response;
            response.Dispose();
            return await base.SendAsync(request, cancellationToken); // same message, as Polly and the resilience handler do
        }
    }

    [Fact]
    public async Task SendAsync_RetriedAfterCacheAddedValidators_ReturnsTheFullResponse()
    {
        var inner = new TestHttpMessageHandler();
        var cache = HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), inner);
        using var client = new HttpClient(new RetryOnceOn5xx { InnerHandler = cache });

        var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1") };
        first.Headers.ETag = new EntityTagHeaderValue("\"e1\""); // validator only: revalidated on every use
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        inner.SetResponses(first, new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), notModified);

        await client.GetStringAsync(Url);
        using var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("v1", await response.Content.ReadAsStringAsync());
    }

    // ---------- Headers the transport adds do not change the store key ----------

    [Fact]
    public async Task SendAsync_VaryAcceptEncodingWithAutomaticDecompression_IsServedFromCache()
    {
        await using var origin = new LocalOrigin(context =>
        {
            context.Response.Headers["Cache-Control"] = "max-age=600";
            context.Response.Headers["Vary"] = "Accept-Encoding";
            return LocalOrigin.Write(context, 200, "body");
        });

        var transport = new SocketsHttpHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), transport));

        Assert.Equal("body", await client.GetStringAsync(origin.Url("/data")));
        Assert.Equal("body", await client.GetStringAsync(origin.Url("/data")));
        Assert.Equal("body", await client.GetStringAsync(origin.Url("/data")));

        Assert.True(origin.Requests.TryPeek(out var firstSeen) && firstSeen.Contains("gzip", StringComparison.OrdinalIgnoreCase),
            "the real transport should have sent Accept-Encoding");
        Assert.Single(origin.Requests);
    }

    // ---------- A Cache-Control the parser rejects is not stored ----------

    private sealed class CookieEchoOrigin(Action<HttpResponseMessage> shape) : HttpMessageHandler
    {
        private int _count;
        public int Count => _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(",", values) : "-";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"cookie={cookie}#{n}"), RequestMessage = request };
            shape(response);
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SendAsync_SharedCacheWithUnparseablePrivateCacheControl_DoesNotShareBetweenUsers()
    {
        var origin = new CookieEchoOrigin(response =>
        {
            response.Headers.TryAddWithoutValidation("Cache-Control", "private; max-age=600"); // ';' typo: whole value invalid
            response.Content.Headers.Expires = DateTimeOffset.UtcNow.AddMinutes(10);
        });
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions { IsSharedCache = true }, origin));

        async Task<string> As(string cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await client.SendAsync(request);
            return await response.Content.ReadAsStringAsync();
        }

        await As("session=alice");
        var bob = await As("session=bob");

        Assert.StartsWith("cookie=session=bob#", bob);
        Assert.Equal(2, origin.Count);
    }

    [Fact]
    public async Task SendAsync_UnparseableNoStoreCacheControl_IsNotStored()
    {
        var origin = new CookieEchoOrigin(response =>
        {
            response.Headers.TryAddWithoutValidation("Cache-Control", "no-store, max-age=-1"); // negative delta-seconds: whole value invalid
            response.Content.Headers.Expires = DateTimeOffset.UtcNow.AddMinutes(10);
        });
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), origin));

        await client.GetStringAsync(Url);
        await client.GetStringAsync(Url);

        Assert.Equal(2, origin.Count);
    }

    // ---------- A GET with a body bypasses the cache ----------

    [Fact]
    public async Task SendAsync_GetWithBody_IsNotAnsweredWithAnotherBodysResponse()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("result-a"), Cacheable("result-b"));

        using var a = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url) { Content = new StringContent("{\"q\":\"a\"}") });
        Assert.Equal("result-a", await a.Content.ReadAsStringAsync());
        using var b = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url) { Content = new StringContent("{\"q\":\"b\"}") });

        Assert.Equal("result-b", await b.Content.ReadAsStringAsync());
    }

    // ---------- A far-future Expires does not overflow ----------

    private static HttpResponseMessage FarFuture(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.Date = DateTimeOffset.UtcNow;
        response.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        response.Content.Headers.Expires = new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
        return response;
    }

    [Fact]
    public async Task SendAsync_FarFutureExpiresForAuthorizedCaller_IsCached()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(FarFuture("v1"));

        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "alice");
            using var response = await client.SendAsync(request);
            Assert.Equal("v1", await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(1, inner.RequestCount);
    }

    [Fact]
    public async Task SendAsync_NotModifiedWithFarFutureExpires_ReturnsTheCachedBody()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1") };
        first.Headers.ETag = new EntityTagHeaderValue("\"e1\""); // validator only: revalidated on next use
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.ETag = new EntityTagHeaderValue("\"e1\"");
        notModified.Headers.Date = DateTimeOffset.UtcNow;
        notModified.Content = new ByteArrayContent([]);
        notModified.Content.Headers.Expires = new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero);
        inner.SetResponses(first, notModified);

        for (var i = 0; i < 2; i++)
        {
            // Authorized: stored as a variant, where the URL entry's Expires is computed as now + TTL
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "alice");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("v1", await response.Content.ReadAsStringAsync());
        }
    }

    // ---------- Background revalidation keeps the request version ----------

    private sealed class VersionRecordingOrigin : HttpMessageHandler
    {
        public ConcurrentQueue<Version> Versions { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Versions.Enqueue(request.Version);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v" + Versions.Count) };
            response.Headers.TryAddWithoutValidation("Cache-Control", "max-age=1, stale-while-revalidate=600");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task SendAsync_BackgroundRevalidation_KeepsTheRequestVersion()
    {
        var time = new ManualTimeProvider();
        var origin = new VersionRecordingOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(
            new HttpCacheOptions { EnableStaleWhileRevalidate = true }, origin, timeProvider: time))
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        await client.GetStringAsync(Url);
        time.Advance(TimeSpan.FromSeconds(30));
        await client.GetStringAsync(Url); // stale, inside the SWR window: revalidated in the background

        await HttpCacheTestFactory.WaitUntilAsync(() => origin.Versions.Count >= 2, "the background revalidation reached the origin");

        Assert.Equal(HttpVersion.Version20, origin.Versions.ToArray()[1]);
    }

    // ---------- The fragment is not part of the key ----------

    [Fact]
    public async Task SendAsync_PostToUrl_InvalidatesTheSameUrlRequestedWithAFragment()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        inner.SetResponses(Cacheable("v1"), new HttpResponseMessage(HttpStatusCode.NoContent), Cacheable("v2"));

        Assert.Equal("v1", await client.GetStringAsync(Url + "#top"));
        using (await client.PostAsync(Url, new StringContent("{}"))) { }

        Assert.Equal("v2", await client.GetStringAsync(Url + "#top"));
    }

    // ---------- Weak ETags are revalidated as weak ----------

    [Fact]
    public async Task SendAsync_StaleEntryWithWeakETag_RevalidatesWithTheWeakETag()
    {
        var (client, inner) = HttpCacheTestFactory.CreateClient(new HttpCacheOptions());
        var first = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1") };
        first.Headers.ETag = new EntityTagHeaderValue("\"abc\"", isWeak: true);
        var notModified = new HttpResponseMessage(HttpStatusCode.NotModified);
        notModified.Headers.ETag = new EntityTagHeaderValue("\"abc\"", isWeak: true);
        inner.SetResponses(first, notModified);

        await client.GetStringAsync(Url);
        await client.GetStringAsync(Url);

        var sent = Assert.Single(inner.Requests[1].Headers.IfNoneMatch);
        Assert.True(sent.IsWeak, $"sent If-None-Match: {sent}");
    }

    private static HttpResponseMessage Cacheable(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(10) };
        return response;
    }

    /// <summary>A real HTTP/1.1 origin on a free localhost port, so the real SocketsHttpHandler does the redirects and decompression.</summary>
    private sealed class LocalOrigin : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly string _prefix;

        /// <summary>The Accept-Encoding header of every request, in order.</summary>
        public ConcurrentQueue<string> Requests { get; } = new();

        public LocalOrigin(Func<HttpListenerContext, Task> handle)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(_prefix);
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch { return; }
                    Requests.Enqueue(context.Request.Headers["Accept-Encoding"] ?? "");
                    try { await handle(context); }
                    catch { try { context.Response.Abort(); } catch { } }
                }
            });
        }

        public string Url(string path) => _prefix.TrimEnd('/') + path;

        public static async Task Write(HttpListenerContext context, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            try { await _loop; } catch { }
        }
    }
}
