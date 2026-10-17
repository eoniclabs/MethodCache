using System.Net;
using System.Net.Http.Headers;
using MethodCache.Core.Runtime.Http;
using MethodCache.Extensibility;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class CoreHttpAcceptanceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registration_EitherOrder_UsesCoreStoreAndLeavesApplicationCacheUntouched(bool coreFirst)
    {
        var services = new ServiceCollection();
        var application = new RecordingApplicationCache(); services.AddSingleton<IDistributedCache>(application);
        if (coreFirst) services.AddMethodCache();
        services.AddHttpClient("api").AddHttpCaching(o => o.AllowUnverifiedTransport = true)
            .ConfigurePrimaryHttpMessageHandler(() => new Origin());
        if (!coreFirst) services.AddMethodCache();
        await using var provider = services.BuildServiceProvider(); using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        Assert.Equal("value", await client.GetStringAsync("https://example.test/a"));
        Assert.Equal("value", await client.GetStringAsync("https://example.test/a"));
        Assert.False(provider.GetRequiredService<HttpResponseStore>().Distributed);
        Assert.Equal(0, application.Reads);
        var stats = await provider.GetRequiredService<ICacheAdministrator>().GetStatsAsync();
        Assert.Equal(1, stats.LocalHttpEntries); Assert.True(stats.LocalHttpBytes > 0);
    }

    [Fact]
    public void Registration_WithoutExplicitCore_FailsAtFirstUse()
    {
        var services = new ServiceCollection(); services.AddHttpClient("api").AddHttpCaching();
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IHttpClientFactory>().CreateClient("api"));
        Assert.Contains("AddMethodCache", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NoOpMethodManager_ProvidesHttpAdministration_WithoutHiddenMethodStore(bool noOpFirst)
    {
        var services = new ServiceCollection();
        if (noOpFirst) services.AddSingleton<ICacheManager, NoOpCacheManager>();
        services.AddMethodCache();
        if (!noOpFirst) services.AddSingleton<ICacheManager, NoOpCacheManager>();
        services.AddHttpClient("api").AddHttpCaching(o => o.AllowUnverifiedTransport = true).ConfigurePrimaryHttpMessageHandler(() => new Origin());
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        await client.GetStringAsync("https://example.test/a");
        Assert.IsType<NoOpCacheManager>(provider.GetRequiredService<ICacheManager>());
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        Assert.Equal(CacheEntryKind.Http, Assert.Single(admin.ListEntries()).Kind);
        Assert.Equal(1, (await admin.GetStatsAsync()).LocalHttpEntries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BypassMutation_EmptyCacheableMethods_RetiresAllOtherReaders(bool cookies)
    {
        var time = new FakeTimeProvider(); using var store = new TestHttpStore(time);
        var origin = new Origin();
        using var reader = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, store, time));
        var options = new HttpCacheOptions { KeepCookies = cookies }; options.CacheableMethods.Clear();
        using var writer = new HttpClient(HttpCacheTestFactory.CreateHandler(options, origin, store, time));
        Assert.Equal("value", await reader.GetStringAsync("https://example.test/a")); origin.Value = "new";
        using (await writer.PostAsync("https://example.test/a", new StringContent("update"))) { }
        Assert.Equal("new", await reader.GetStringAsync("https://example.test/a"));
        Assert.Equal(3, origin.Calls);
    }

    [Fact]
    public async Task KeepCookies_OnlyIfCached_NeverRunsOriginOrSelectsStoredResponse()
    {
        var time = new FakeTimeProvider(); using var store = new TestHttpStore(time); var origin = new Origin();
        using var normal = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, store, time));
        await normal.GetStringAsync("https://example.test/a");
        using var bypass = new HttpClient(HttpCacheTestFactory.CreateHandler(new() { KeepCookies = true }, origin, store, time));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/a"); request.Headers.CacheControl = new() { OnlyIfCached = true };
        using var result = await bypass.SendAsync(request);
        Assert.Equal(HttpStatusCode.GatewayTimeout, result.StatusCode); Assert.Equal(1, origin.Calls);
    }

    [Fact]
    public async Task StaticTagsAndEscapedClientNames_RetireOnlyIntendedClients()
    {
        var services = new ServiceCollection(); services.AddMethodCache();
        var origin = new Origin();
        foreach (var name in new[] { "a:b", "a\\:b" })
            services.AddHttpClient(name).AddHttpCaching(o => { o.AllowUnverifiedTransport = true; if (name == "a:b") o.Tags.Add("orders"); })
                .ConfigurePrimaryHttpMessageHandler(() => origin);
        await using var provider = services.BuildServiceProvider(); var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var a = factory.CreateClient("a:b"); using var b = factory.CreateClient("a\\:b");
        const string url = "https://example.test/a"; await a.GetStringAsync(url); await b.GetStringAsync(url);
        origin.Value = "new";
        await provider.GetRequiredService<ICacheAdministrator>().InvalidateTagsAsync(["http:" + MethodCacheKeys.Part("a:b")]);
        Assert.Equal("new", await a.GetStringAsync(url)); Assert.Equal("value", await b.GetStringAsync(url));
        origin.Value = "latest"; await provider.GetRequiredService<ICacheAdministrator>().InvalidateTagsAsync(["orders"]);
        Assert.Equal("latest", await a.GetStringAsync(url)); Assert.Equal("value", await b.GetStringAsync(url));
    }

    [Fact]
    public async Task LargeBodyBeyondEntireHttpBudget_RemainsStreamedAndUnstored()
    {
        using var store = new TestHttpStore(maximumBytes: 1024);
        var body = new CountingStream(5000); var origin = new StreamingOrigin(body);
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, store));
        using var response = await client.GetAsync("https://example.test/a", HttpCompletionOption.ResponseHeadersRead);
        Assert.InRange(body.ReadBytes, 0, 1025);
        Assert.Equal(5000, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(0, store.Core.Observe().LocalHttpEntries);
    }

    [Fact]
    public async Task MethodKeysWithHttpText_AreStillOrdinaryRemovableMethodEntries()
    {
        var services = new ServiceCollection(); services.AddMethodCache(); services.AddHttpClient("api").AddHttpCaching();
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<MethodCache.Core.Storage.Abstractions.IMemoryCache>().SetAsync("http:manual", 42, TimeSpan.FromMinutes(1));
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        Assert.Equal(CacheEntryKind.Method, admin.GetEntry("http:manual")!.Kind);
        Assert.True(await admin.RemoveEntryAsync("http:manual"));
        Assert.Null(admin.GetEntry("http:manual"));
    }

    [Fact]
    public async Task AdminIdCollision_RemovesTheSameKindThatGetEntryReports()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        var origin = new Origin();
        services.AddHttpClient("api").AddHttpCaching(options => options.AllowUnverifiedTransport = true)
            .ConfigurePrimaryHttpMessageHandler(() => origin);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        await client.GetStringAsync("https://example.test/a");
        var admin = provider.GetRequiredService<ICacheAdministrator>();
        var id = Assert.Single(admin.ListEntries()).Key;
        var memory = provider.GetRequiredService<MethodCache.Core.Storage.Abstractions.IMemoryCache>();
        await memory.SetAsync(id, 42, TimeSpan.FromMinutes(1));
        Assert.Equal(CacheEntryKind.Method, admin.GetEntry(id)!.Kind);
        Assert.True(await admin.RemoveEntryAsync(id));
        Assert.False((await provider.GetRequiredService<ICacheManager>().TryGetAsync<int>(id)).Found);
        Assert.Equal(CacheEntryKind.Http, admin.GetEntry(id)!.Kind);
        await client.GetStringAsync("https://example.test/a");
        Assert.Equal(1, origin.Calls);
        Assert.True(await admin.RemoveEntryAsync(id));
        await client.GetStringAsync("https://example.test/a");
        Assert.Equal(2, origin.Calls);
    }

    [Fact]
    public async Task HttpAndMethodBudgets_EvictIndependently()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(cache => cache.Memory(o => { o.MaxItems = 2; o.MaxHttpBytes = 4096; }));
        services.AddHttpClient("api").AddHttpCaching(o => o.AllowUnverifiedTransport = true).ConfigurePrimaryHttpMessageHandler(() => new Origin());
        await using var provider = services.BuildServiceProvider();
        var memory = provider.GetRequiredService<MethodCache.Core.Storage.Abstractions.IMemoryCache>();
        await memory.SetAsync("method", 42, TimeSpan.FromMinutes(1));
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        for (var i = 0; i < 30; i++) await client.GetStringAsync("https://example.test/" + i);
        var admin = provider.GetRequiredService<ICacheAdministrator>(); var before = await admin.GetStatsAsync();
        Assert.Equal(42, await memory.GetAsync<int>("method"));
        Assert.InRange(before.LocalHttpBytes, 1, 4096); Assert.InRange(before.LocalHttpEntries, 1, 10);
        for (var i = 0; i < 30; i++) await memory.SetAsync("method:" + i, i, TimeSpan.FromMinutes(1));
        var after = await admin.GetStatsAsync();
        Assert.Equal(before.LocalHttpEntries, after.LocalHttpEntries); Assert.Equal(before.LocalHttpBytes, after.LocalHttpBytes);
    }

    [Fact]
    public async Task DisabledTinyMethodPolicy_DoesNotChangeHttpFreshness()
    {
        var time = new FakeTimeProvider(); var origin = new Origin(); var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddMethodCache(cache => cache.Defaults(o => o.WithEnabled(false).WithDuration(TimeSpan.FromTicks(1))));
        services.AddHttpClient("api").AddHttpCaching(o => o.AllowUnverifiedTransport = true).ConfigurePrimaryHttpMessageHandler(() => origin);
        await using var provider = services.BuildServiceProvider(); using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        Assert.Equal("value", await client.GetStringAsync("https://example.test/a")); origin.Value = "changed";
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("value", await client.GetStringAsync("https://example.test/a")); Assert.Equal(1, origin.Calls);
    }

    [Fact]
    public async Task UnknownLengthBody_BudgetBoundsReadBeforeReturningStreamToCaller()
    {
        using var store = new TestHttpStore(maximumBytes: 8192);
        var body = new UnknownLengthStream(50000);
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), new StreamingOrigin(body), store));
        using var response = await client.GetAsync("https://example.test/a", HttpCompletionOption.ResponseHeadersRead);
        Assert.InRange(body.ReadBytes, 1, 8193);
        Assert.Equal(50000, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal(0, store.Core.Observe().LocalHttpEntries);
    }

    [Fact]
    public async Task LongClientNames_AreAcceptedAndKeepAdministrationBounded()
    {
        using var store = new TestHttpStore(); var origin = new Origin();
        using var accepted = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, store, clientName: new string('a', 1024)));
        await accepted.GetStringAsync("https://example.test/a?private=canary");
        Assert.All(store.Core.List(), entry => Assert.Equal(69, entry.Id.Length));
        using var longer = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, store, clientName: new string('a', 1025)));
        await longer.GetStringAsync("https://example.test/a");
        Assert.Equal(2, origin.Calls);
        Assert.All(store.Core.List(), entry => Assert.True(entry.Client.Length <= 256));
    }

    private sealed class Origin : HttpMessageHandler
    {
        internal string Value = "value"; internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Post ? HttpStatusCode.NoContent : HttpStatusCode.OK)
            { Content = new StringContent(Value), Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) } } });
        }
    }
    private sealed class StreamingOrigin(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body), Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) } } });
    }
    private sealed class CountingStream(int length) : MemoryStream(new byte[length])
    {
        internal int ReadBytes;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = base.ReadAsync(buffer, token); ReadBytes += read.Result; return read; }
    }
    private sealed class UnknownLengthStream(int length) : MemoryStream(new byte[length])
    {
        public override bool CanSeek => false;
        internal int ReadBytes;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var read = base.ReadAsync(buffer, token); ReadBytes += read.Result; return read; }
    }

    private sealed class RecordingApplicationCache : IDistributedCache
    {
        internal int Reads;
        public byte[]? Get(string key) { Reads++; return null; }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] bytes, DistributedCacheEntryOptions options) => throw new InvalidOperationException("Application cache must not be used");
        public Task SetAsync(string key, byte[] bytes, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("Application cache must not be used");
        public void Remove(string key) => throw new InvalidOperationException("Application cache must not be used");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("Application cache must not be used");
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
