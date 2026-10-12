using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

/// <summary>
/// Pins the threats listed for MethodCache.HttpCaching in docs/security.md: one caller never gets a response that
/// was fetched for another, in a private or a shared cache, and a cached response never hands out a session.
/// </summary>
public class HttpCacheIsolationTests
{
    private const string Url = "https://api.example.com/me";

    /// <summary>
    /// An origin that answers every request with who asked (its Authorization and Cookie headers, X-Tenant and
    /// Accept-Language) and a counter, so a test can tell whose response a caller got and whether it is new.
    /// </summary>
    private sealed class EchoOrigin : HttpMessageHandler
    {
        private int _count;

        public Action<HttpResponseMessage>? Shape { get; set; }

        public int Count => _count;

        public static string Identity(HttpRequestMessage request)
        {
            string Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : "-";
            return $"auth={Header("Authorization")};cookie={Header("Cookie")};tenant={Header("X-Tenant")};lang={Header("Accept-Language")}";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _count);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{Identity(request)}#{n}"),
                RequestMessage = request,
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            Shape?.Invoke(response);
            return Task.FromResult(response);
        }
    }

    private static (HttpClient Client, EchoOrigin Origin) Create(HttpCacheOptions options, IHttpCacheStorage? storage = null)
    {
        var origin = new EchoOrigin();
        var handler = HttpCacheTestFactory.CreateHandler(options, origin, storage);
        return (new HttpClient(handler), origin);
    }

    private static HttpRequestMessage Get(string? authorization = null, string? cookie = null, string? tenant = null, string? language = null, string url = Url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (authorization != null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (cookie != null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (tenant != null)
        {
            request.Headers.TryAddWithoutValidation("X-Tenant", tenant);
        }

        if (language != null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", language);
        }

        return request;
    }

    /// <summary>Sends <paramref name="request"/> and checks the body was made for the same caller.</summary>
    private static async Task<string> SendAsOwnerAsync(HttpClient client, HttpRequestMessage request)
    {
        var identity = EchoOrigin.Identity(request);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(body.StartsWith(identity + "#", StringComparison.Ordinal), $"Request as [{identity}] got a response made for [{body}]");
        return body;
    }

    public static TheoryData<bool, string> CallerHeaders => new()
    {
        { false, "Authorization" },
        { false, "Cookie" },
        { true, "Authorization" },
    };

    [Theory]
    [MemberData(nameof(CallerHeaders))]
    public async Task ManyCallers_InterleavedRequests_EachGetOnlyTheirOwnResponses(bool shared, string header)
    {
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = shared });

        // Values that don't parse as Authorization (no scheme, a cookie) count as credentials too
        var callers = new List<string?>
        {
            "Bearer alice", "Bearer alicE", "Bearer bob", "Bearer bob,alice", "Basic YWxpY2U6", "alice-api-key",
            "session=alice", "session=alice; admin=1", "session=bob", "",
        };
        if (!shared)
        {
            callers.Add(null); // anonymous; in a shared cache see SharedCache_ServesAStoredAnonymousResponse_ToAuthorizedRequests
        }

        var random = new Random(20261011);
        var firstBody = new Dictionary<string, string>();

        for (var i = 0; i < 300; i++)
        {
            var caller = callers[random.Next(callers.Count)];
            var request = header == "Cookie" ? Get(cookie: caller) : Get(authorization: caller);
            var body = await SendAsOwnerAsync(client, request);

            // In a private cache every caller keeps the response it was first given while it is fresh
            if (!shared)
            {
                Assert.Equal(firstBody.TryAdd(caller ?? "<none>", body) ? body : firstBody[caller ?? "<none>"], body);
            }
        }

        // A shared cache stores none of them: they are responses to authorized requests without public
        Assert.Equal(shared ? 300 : callers.Count, origin.Count);
    }

    [Fact]
    public async Task SharedCache_ServesAStoredAnonymousResponse_ToAuthorizedRequests()
    {
        // RFC 9111 limits which responses a shared cache stores, not who it serves them to: a response stored for an
        // anonymous request is the URL's public response, and authorized requests get it too unless the origin
        // sends Vary: Authorization. Never someone else's private data, but not a personalised response either.
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = true });
        using var anonymous = await client.SendAsync(Get());
        using var alice = await client.SendAsync(Get(authorization: "Bearer alice"));
        Assert.Equal(await anonymous.Content.ReadAsStringAsync(), await alice.Content.ReadAsStringAsync());
        Assert.Equal(1, origin.Count);

        var (varied, variedOrigin) = Create(new HttpCacheOptions { IsSharedCache = true });
        variedOrigin.Shape = r => r.Headers.Vary.Add("Authorization");
        await SendAsOwnerAsync(varied, Get());
        await SendAsOwnerAsync(varied, Get(authorization: "Bearer alice"));
        Assert.Equal(2, variedOrigin.Count);
    }

    [Fact]
    public async Task SharedCache_DoesNotStoreAuthorizedResponses_UnlessTheyAllowIt()
    {
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = true });

        await SendAsOwnerAsync(client, Get(authorization: "Bearer alice"));
        await SendAsOwnerAsync(client, Get(authorization: "Bearer bob"));
        await SendAsOwnerAsync(client, Get(authorization: "Bearer alice"));

        Assert.Equal(3, origin.Count);
    }

    [Fact]
    public async Task SharedCache_DoesNotStorePrivateResponses()
    {
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = true });
        origin.Shape = r => r.Headers.CacheControl!.Private = true;

        await SendAsOwnerAsync(client, Get(cookie: "session=alice"));
        await SendAsOwnerAsync(client, Get(cookie: "session=bob"));
        await SendAsOwnerAsync(client, Get());

        Assert.Equal(3, origin.Count);
    }

    [Fact]
    public async Task SharedCache_ServesAnAuthorizedResponseMarkedPublic_ToEveryone()
    {
        // RFC 9111 section 3.5: public (or s-maxage, must-revalidate) on a response to an authorized request
        // says the origin allows sharing it. This is documented, not a leak.
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = true });
        origin.Shape = r => r.Headers.CacheControl!.Public = true;

        using var first = await client.SendAsync(Get(authorization: "Bearer alice"));
        using var second = await client.SendAsync(Get(authorization: "Bearer bob"));

        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, origin.Count);
    }

    [Fact]
    public async Task SharedCache_DoesNotKeyOnCookies_UnlessTheOriginVariesOnThem()
    {
        // A shared cache follows the origin: a cookie-personalised response without private or Vary: Cookie is
        // shared. KeyHeaders (or Vary: Cookie from the origin) keeps cookie callers apart.
        var (shared, sharedOrigin) = Create(new HttpCacheOptions { IsSharedCache = true });
        using var alice = await shared.SendAsync(Get(cookie: "session=alice"));
        using var bob = await shared.SendAsync(Get(cookie: "session=bob"));
        Assert.Equal(await alice.Content.ReadAsStringAsync(), await bob.Content.ReadAsStringAsync());
        Assert.Equal(1, sharedOrigin.Count);

        var keyed = new HttpCacheOptions { IsSharedCache = true };
        keyed.KeyHeaders.Add("Cookie");
        var (client, origin) = Create(keyed);
        await SendAsOwnerAsync(client, Get(cookie: "session=alice"));
        await SendAsOwnerAsync(client, Get(cookie: "session=bob"));
        await SendAsOwnerAsync(client, Get(cookie: "session=alice"));
        Assert.Equal(2, origin.Count);

        var (varied, variedOrigin) = Create(new HttpCacheOptions { IsSharedCache = true });
        variedOrigin.Shape = r => r.Headers.Vary.Add("Cookie");
        await SendAsOwnerAsync(varied, Get(cookie: "session=alice"));
        await SendAsOwnerAsync(varied, Get(cookie: "session=bob"));
        await SendAsOwnerAsync(varied, Get(cookie: "session=alice"));
        Assert.Equal(2, variedOrigin.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyHeaders_KeepTenantsApart(bool shared)
    {
        var options = new HttpCacheOptions { IsSharedCache = shared };
        options.KeyHeaders.Add("X-Tenant");
        var (client, origin) = Create(options);
        var random = new Random(7);
        string?[] tenants = { null, "contoso", "fabrikam", "Contoso", "contoso,fabrikam", "" };

        for (var i = 0; i < 100; i++)
        {
            await SendAsOwnerAsync(client, Get(tenant: tenants[random.Next(tenants.Length)]));
        }

        Assert.Equal(tenants.Length, origin.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Vary_KeepsVariantsApart(bool shared)
    {
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = shared });
        origin.Shape = r => r.Headers.Vary.Add("Accept-Language");
        string?[] languages = { null, "en", "sv", "en, sv", "EN" };
        var random = new Random(11);

        for (var i = 0; i < 100; i++)
        {
            await SendAsOwnerAsync(client, Get(language: languages[random.Next(languages.Length)]));
        }

        Assert.Equal(languages.Length, origin.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VaryStar_IsNeverServedFromTheCache(bool shared)
    {
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = shared });
        origin.Shape = r => r.Headers.Vary.Add("*");

        await SendAsOwnerAsync(client, Get());
        await SendAsOwnerAsync(client, Get());

        Assert.Equal(2, origin.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetCookie_IsNeverReplayedFromTheCache(bool shared)
    {
        // The caller that made the request gets the origin's Set-Cookie; anyone answered from the cache must not
        // get that session
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = shared });
        origin.Shape = r =>
        {
            r.Headers.CacheControl!.Public = true;
            r.Headers.TryAddWithoutValidation("Set-Cookie", "session=s3cr3t; Path=/; HttpOnly");
            r.Headers.TryAddWithoutValidation("X-Other", "kept");
        };

        using var live = await client.SendAsync(Get());
        using var cached = await client.SendAsync(Get());

        Assert.Equal(1, origin.Count);
        Assert.True(live.Headers.Contains("Set-Cookie"));
        Assert.False(cached.Headers.Contains("Set-Cookie"));
        Assert.True(cached.Headers.Contains("X-Other"));
    }

    [Fact]
    public async Task SetCookie_OnANotModified_ReachesThatCaller_ButIsNotStored()
    {
        var origin = new EchoOrigin
        {
            Shape = r =>
            {
                r.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                r.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            }
        };
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), new NotModifiedAfterFirstCall(origin)));

        using var first = await client.GetAsync(Url);
        using var revalidated = await client.GetAsync(Url);   // the 304 carries a Set-Cookie for this caller
        using var third = await client.GetAsync(Url);         // another 304, without one

        Assert.Equal(await first.Content.ReadAsStringAsync(), await third.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "session=s3cr3t" }, revalidated.Headers.GetValues("Set-Cookie"));
        Assert.False(third.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task AWrite_InvalidatesEveryCallersCopy_AndNeverServesAnotherCallersResponse()
    {
        var (client, origin) = Create(new HttpCacheOptions());

        var alice1 = await SendAsOwnerAsync(client, Get(authorization: "Bearer alice"));
        var bob1 = await SendAsOwnerAsync(client, Get(authorization: "Bearer bob"));
        await SendAsOwnerAsync(client, Get());

        using (await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, Url) { Headers = { { "Authorization", "Bearer alice" } } }))
        {
        }

        // Bob reads first after the write: he starts a new generation, and alice's old variant is gone with it
        var bob2 = await SendAsOwnerAsync(client, Get(authorization: "Bearer bob"));
        var alice2 = await SendAsOwnerAsync(client, Get(authorization: "Bearer alice"));
        var anonymous2 = await SendAsOwnerAsync(client, Get());

        Assert.NotEqual(bob1, bob2);
        Assert.NotEqual(alice1, alice2);
        Assert.EndsWith("#5", bob2);          // the POST was #4
        Assert.EndsWith("#6", alice2);
        Assert.EndsWith("#7", anonymous2);
        Assert.Equal(alice2, await SendAsOwnerAsync(client, Get(authorization: "Bearer alice")));
        Assert.Equal(bob2, await SendAsOwnerAsync(client, Get(authorization: "Bearer bob")));
    }

    [Theory]
    [InlineData("https://api.example.com/users/a%3Fx", "https://api.example.com/users/a?x")]
    [InlineData("https://api.example.com/a%2Fb", "https://api.example.com/a/b")]
    [InlineData("https://api.example.com/a%23b", "https://api.example.com/a")]
    [InlineData("https://api.example.com/a?q=1%262", "https://api.example.com/a?q=1&2")]
    [InlineData("https://api.example.com/a?q=%3D", "https://api.example.com/a?q==")]
    [InlineData("https://api.example.com/A", "https://api.example.com/a")]
    [InlineData("https://api.example.com:8443/a", "https://api.example.com/a")]
    [InlineData("http://api.example.com/a", "https://api.example.com/a")]
    public async Task UrlsThatDifferOnlyInEscaping_DoNotShareAnEntry(string first, string second)
    {
        var (client, origin) = Create(new HttpCacheOptions { IsSharedCache = true });

        await client.GetAsync(first);
        await client.GetAsync(second);

        Assert.Equal(2, origin.Count);
        Assert.NotEqual(
            HttpCacheKeys.ForUri("c", HttpMethod.Get, new Uri(first)),
            HttpCacheKeys.ForUri("c", HttpMethod.Get, new Uri(second)));
    }

    [Fact]
    public async Task Metrics_CarryOnlyTheClientNameAndOutcome_NeverTheUrl()
    {
        var tags = new ConcurrentBag<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name.StartsWith("methodcache.http.", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, t, _) => { foreach (var tag in t) { tags.Add(tag); } });
        listener.SetMeasurementEventCallback<double>((_, _, t, _) => { foreach (var tag in t) { tags.Add(tag); } });
        listener.Start();

        using var metrics = new HttpCacheMetrics();
        var origin = new EchoOrigin();
        var options = new HttpCacheOptions { AllowUnverifiedTransport = true }; // Verified test transport.
        var handler = new HttpCacheHandler(
            new HttpCacheClientState("orders-api"),
            HttpCacheTestFactory.CreateInMemoryStorage(),
            new TestOptionsMonitor<HttpCacheOptions>(options),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpCacheHandler>.Instance,
            metrics)
        { InnerHandler = origin };
        using var client = new HttpClient(handler);

        await client.SendAsync(Get(authorization: "Bearer s3cr3t", url: "https://api.example.com/users/alice@example.com?ssn=123"));
        await client.SendAsync(Get(authorization: "Bearer s3cr3t", url: "https://api.example.com/users/alice@example.com?ssn=123"));

        Assert.NotEmpty(tags);
        Assert.All(tags, tag => Assert.Contains(tag.Key, new[] { "client", "outcome" }));
        Assert.Equal(new[] { "orders-api" }, tags.Where(t => t.Key == "client").Select(t => (string)t.Value!).Distinct());
    }

    /// <summary>Lets the first request through, then answers 304, with a Set-Cookie the first time only.</summary>
    private sealed class NotModifiedAfterFirstCall(HttpMessageHandler origin) : DelegatingHandler(origin)
    {
        private int _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var notModified = new HttpResponseMessage(HttpStatusCode.NotModified) { RequestMessage = request };
            notModified.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            if (call == 2)
            {
                notModified.Headers.TryAddWithoutValidation("Set-Cookie", "session=s3cr3t");
            }

            return notModified;
        }
    }
}
