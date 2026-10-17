using System.Net;
using System.Net.Http.Headers;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Runtime.Http;
using MethodCache.Core.Telemetry;
using MethodCache.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.TestSupport;

// Linked into the Redis/SQL integration suites. These assertions use real native operations and deliberately
// drop every backplane message. The format-3 store models a future HTTP value namespace, sharing v1 epochs.
internal static class HttpNativeContract
{
    internal static void Configure(ServiceCollection services, Origin origin, byte format, bool http)
    {
        services.AddMethodCache();
        services.RemoveAllKeyed<IBackplane>(MethodCache.Infrastructure.OwnedServices.Key);
        services.AddKeyedSingleton<IBackplane>(MethodCache.Infrastructure.OwnedServices.Key, new DroppingBackplane());
        if (!http) return;
        services.AddSingleton<ICacheManager, NoOpCacheManager>();
        services.AddSingleton(sp => new HttpResponseStore(sp.GetRequiredService<IOptions<MethodCacheMemoryOptions>>(),
            sp.GetRequiredService<CoreTelemetry>(), sp.GetService<TimeProvider>(), sp.GetRequiredService<DistributedCacheLayer>(), format));
        services.AddHttpClient("api").AddHttpCaching(options =>
        {
            options.AllowUnverifiedTransport = true;
            options.Tags.Add("static-http");
        }).ConfigurePrimaryHttpMessageHandler(() => origin);
    }

    internal static async Task RunAsync(Func<Origin, byte, bool, Task<ServiceProvider>> create)
    {
        using var origin = new Origin();
        await using var first = await create(origin, 2, true);
        await using var second = await create(origin, 2, true);
        await using var future = await create(origin, 3, true);
        await using var coreOnly = await create(origin, 2, false);
        using var a = first.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        using var b = second.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        using var c = future.GetRequiredService<IHttpClientFactory>().CreateClient("api");
        var mutator = coreOnly.GetRequiredService<ICacheManager>();
        var admin = second.GetRequiredService<ICacheAdministrator>();
        const string url = "https://example.test/native?private=query-canary";
        Assert.Equal("old:alice", await Read(a, url));
        Assert.Equal("old:alice", await Read(b, url));
        Assert.Equal(1, origin.Reads); // The second node actually shared the first node's L2 value.
        Assert.Equal("old:alice", await Read(c, url));
        Assert.Equal(2, origin.Reads); // The simulated future value format has a separate namespace.
        Assert.IsType<NoOpCacheManager>(second.GetRequiredService<ICacheManager>());
        var stats = await admin.GetStatsAsync();
        Assert.Equal(0, stats.LocalMethodEntries); Assert.Equal(0, stats.LocalHttpEntries); Assert.Equal(0, stats.LocalHttpBytes);
        Assert.True(stats.ObservedDistributedEntries > 0);
        var observation = Assert.Single(admin.ListEntries());
        Assert.Equal(CacheEntryStorage.ObservedDistributed, observation.Storage);
        Assert.Contains("Historical", observation.Description);
        Assert.DoesNotContain("query-canary", System.Text.Json.JsonSerializer.Serialize(observation));

        origin.Version = "tagged";
        await mutator.InvalidateByTagsAsync("static-http"); // This node has never loaded the HTTP add-on.
        Assert.Equal("tagged:alice", await Read(a, url));
        Assert.Equal("tagged:alice", await Read(b, url));
        Assert.Equal("tagged:alice", await Read(c, url));
        origin.Version = "cleared";
        await coreOnly.GetRequiredService<ICacheAdministrator>().ClearAsync();
        Assert.Equal("cleared:alice", await Read(a, url));
        Assert.Equal("cleared:alice", await Read(c, url));
        await Read(b, url);
        origin.Version = "removed";
        var known = admin.ListEntries().OrderBy(info => info.TimeToLive).Last();
        Assert.True(await admin.RemoveEntryAsync(known.Key));
        Assert.Equal("removed:alice", await Read(a, url));
        Assert.Equal("removed:alice", await Read(c, url));
        Assert.False(await admin.RemoveEntryAsync("http:" + new string('0', 64)));

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            foreach (var revalidate in new[] { false, true })
            {
                var address = "https://example.test/race/" + method.Method + "/" + revalidate;
                origin.Version = "before";
                if (revalidate) Assert.Equal("before:alice", await Read(a, address));
                var gate = origin.Hold(address, revalidate);
                var pending = Read(a, address, force: revalidate);
                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
                origin.Version = "after";
                using var request = new HttpRequestMessage(method, address);
                using var changed = await c.SendAsync(request);
                Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
                gate.Release.TrySetResult();
                Assert.Equal("before:alice", await pending);
                Assert.Equal("after:alice", await Read(b, address));
                Assert.Equal("after:alice", await Read(c, address));
                Assert.Equal("after:bob", await Read(a, address, user: "bob"));
            }
        }
        Assert.Equal(8, origin.Mutations);
    }

    private static async Task<string> Read(HttpClient client, string url, bool force = false, string user = "alice")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user);
        request.Headers.AcceptLanguage.ParseAdd("sv");
        if (force) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    internal sealed class Gate(string url, bool notModified)
    {
        internal readonly string Url = url;
        internal readonly bool NotModified = notModified;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal sealed class Origin : HttpMessageHandler
    {
        internal string Version = "old";
        internal int Reads, Mutations;
        private Gate? _gate;
        internal Gate Hold(string url, bool notModified)
        {
            var gate = new Gate(url, notModified); Volatile.Write(ref _gate, gate); return gate;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            {
                Interlocked.Increment(ref Mutations);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            Interlocked.Increment(ref Reads);
            var version = Volatile.Read(ref Version);
            var user = request.Headers.Authorization?.Parameter ?? "anonymous";
            var gate = Volatile.Read(ref _gate);
            var held = gate != null && gate.Url == request.RequestUri!.ToString() &&
                Interlocked.CompareExchange(ref _gate, null, gate) == gate;
            if (held)
            {
                gate!.Started.TrySetResult();
                await gate.Release.Task.WaitAsync(token);
            }
            var response = new HttpResponseMessage(held && gate!.NotModified ? HttpStatusCode.NotModified : HttpStatusCode.OK)
            {
                Content = new StringContent(version + ":" + user),
                Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) }, ETag = new EntityTagHeaderValue("\"v1\"") }
            };
            response.Headers.Vary.Add("Accept-Language");
            return response;
        }
    }
    private sealed class DroppingBackplane : IBackplane
    {
        public BackplaneState ConnectionState => BackplaneState.Connected;
        public string InstanceId { get; } = Guid.NewGuid().ToString("N");
        public Task PublishInvalidationAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken token = default) => Task.CompletedTask;
        public Task PublishClearAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task SubscribeAsync(Func<BackplaneMessage, Task> callback, CancellationToken token = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken token = default) => Task.CompletedTask;
    }
}
