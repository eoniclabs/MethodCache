using System.Net;
using System.Net.Http.Headers;
using MethodCache.HttpCaching.Internal;
using MethodCache.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpGenerationFaultTests
{
    [Fact]
    public async Task HangingGenerationRead_IsBoundedByFakeClock_AndBypassesOriginPublication()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time); backend.HoldNextRead = true;
        using var storage = new TestHttpStore(time, backend); using var origin = new Origin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, storage, time));
        var pending = client.GetAsync("https://example.test/a"); await backend.ReadStarted.Task;
        Assert.Equal(0, origin.Reads); time.Advance(TimeSpan.FromSeconds(1));
        using var result = await pending;
        Assert.Equal("value", await result.Content.ReadAsStringAsync()); Assert.Equal(1, origin.Reads);
        Assert.Empty(storage.Core.List());
        backend.ReadRelease.TrySetResult();
    }

    [Fact]
    public async Task HangingAdvance_AfterSuccessfulOrigin_ThrowsTypedError_AndLaterRecoversWithoutRepeatingMutation()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time); backend.HoldNextAdvance = true;
        using var storage = new TestHttpStore(time, backend); using var origin = new Origin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, storage, time));
        var pending = client.PostAsync("https://example.test/a", new StringContent("change"));
        await backend.Started.Task; Assert.Equal(1, origin.Mutations);
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<HttpCacheInvalidationException>(() => pending);
        Assert.Equal(1, storage.Core.PendingRecoveries); Assert.True(origin.ResponseDisposed);
        time.Advance(TimeSpan.FromSeconds(11)); await storage.Core.FlushRecoveryAsync();
        Assert.Equal(0, storage.Core.PendingRecoveries); Assert.Equal(1, origin.Mutations);
    }

    [Fact]
    public async Task CancellationAfterOrigin_StartsRecoveryForEverySameOriginTarget_AndDisposesResponse()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time); backend.HoldNextAdvance = true;
        using var storage = new TestHttpStore(time, backend); using var origin = new Origin { IncludeTargets = true };
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, storage, time));
        var options = new HttpCacheOptions();
        using var caller = new CancellationTokenSource();
        var pending = client.PostAsync("https://example.test/a", new StringContent("change"), caller.Token);
        await backend.Started.Task; caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, origin.Mutations); Assert.True(origin.ResponseDisposed); Assert.Equal(3, backend.Advances);
        backend.Release.TrySetResult();
        time.Advance(TimeSpan.FromSeconds(11)); await storage.Core.FlushRecoveryAsync();
        Assert.Equal(1, origin.Mutations);
    }

    [Fact]
    public async Task PendingCommonTagRecovery_AfterUnrelatedKeyAcknowledgement_NeverSelectsOldHttpGeneration()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend);
        _ = await storage.GetGenerationAsync("url");
        backend.FailStamp = true;
        await storage.Layer.RemoveAsync(["ordinary-method-key"]);
        await storage.Layer.InvalidateTagsAsync(["http:test"]);
        backend.FailStamp = false; backend.HoldStampAddress = storage.Layer.HttpTagAddress("http:test");
        time.Advance(TimeSpan.FromSeconds(10)); await backend.StampStarted.Task;
        try { Assert.StartsWith("unavailable:", await storage.GetGenerationAsync("url")); }
        finally { backend.StampRelease.TrySetResult(); await storage.Layer.FlushAsync(force: true); }
        Assert.DoesNotContain("unavailable:", await storage.GetGenerationAsync("url"));
    }

    private sealed class Origin : HttpMessageHandler
    {
        internal int Reads, Mutations; internal bool ResponseDisposed, IncludeTargets;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref Reads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("value"), Headers = { CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(1) } } });
            }
            Interlocked.Increment(ref Mutations);
            var result = new HttpResponseMessage(HttpStatusCode.NoContent) { Content = new OwnedContent(() => ResponseDisposed = true) };
            if (IncludeTargets) { result.Headers.Location = new Uri("https://example.test/b"); result.Content.Headers.ContentLocation = new Uri("https://example.test/c"); }
            return Task.FromResult(result);
        }
    }
    private sealed class OwnedContent(Action disposed) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) disposed(); }
    }
}
