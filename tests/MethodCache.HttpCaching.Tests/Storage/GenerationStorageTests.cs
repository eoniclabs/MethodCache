using MethodCache.TestSupport;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class GenerationStorageTests
{
    [Fact]
    public async Task Generation_SharedAcrossInstances_RemainsPermanentAcrossClockAdvance()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var first = new TestHttpStore(time, backend); using var second = new TestHttpStore(time, backend);
        await first.InvalidateAsync("url");
        var generation = await second.GetGenerationAsync("url");
        time.Advance(TimeSpan.FromDays(365));
        Assert.Equal(generation, await second.GetGenerationAsync("url"));
        Assert.Equal(3, backend.MetadataCount);
    }

    [Fact]
    public async Task Invalidation_CompletedOutOfOrder_NeverRestoresObservedGeneration()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time); backend.HoldNextAdvance = true;
        using var first = new TestHttpStore(time, backend); using var second = new TestHttpStore(time, backend);
        var older = first.InvalidateAsync("url").AsTask(); await backend.Started.Task;
        await second.InvalidateAsync("url"); var newer = await second.GetGenerationAsync("url");
        backend.Release.TrySetResult(); await older;
        Assert.NotEqual(newer, await second.GetGenerationAsync("url"));
        Assert.Equal(2, backend.Advances);
    }

    [Fact]
    public async Task Invalidation_AdvanceFailure_ReportsUnconfirmedAndOwnsRecovery()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend);
        var before = await storage.GetGenerationAsync("url"); backend.FailAdvance = true;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => storage.InvalidateAsync("url").AsTask());
        Assert.Equal(1, storage.Core.PendingRecoveries);
        Assert.StartsWith("unavailable:", await storage.GetGenerationAsync("url"));
        backend.FailAdvance = false; time.Advance(TimeSpan.FromSeconds(11));
        await storage.Core.FlushRecoveryAsync();
        Assert.Equal(0, storage.Core.PendingRecoveries);
        Assert.NotEqual(before, await storage.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task Invalidation_ManyDistinctUrls_BoundsPermanentMetadata()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend);
        for (var i = 0; i < 20000; i++) await storage.InvalidateAsync("url:" + i);
        Assert.InRange(backend.MetadataCount, 1, 4096);
    }

    [Fact]
    public async Task Generation_ReadFailure_NeverReusesPreviousNamespace()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend);
        var before = await storage.GetGenerationAsync("url"); backend.FailRead = true;
        var failed = await storage.GetGenerationAsync("url");
        Assert.StartsWith("unavailable:", failed);
        Assert.NotEqual(before, failed); Assert.NotEqual(failed, await storage.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task Generation_MetadataLostAfterMutation_NeverRestoresOldResponse()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend); using var origin = new VersionedOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, storage, time));
        const string url = "https://api.example.com/resource";
        Assert.Equal("old", await client.GetStringAsync(url)); origin.Version = "new";
        using (await client.PostAsync(url, new StringContent("update"))) { }
        Assert.Equal("new", await client.GetStringAsync(url)); backend.LoseMetadata();
        Assert.Equal("new", await client.GetStringAsync(url));
    }

    [Fact]
    public async Task Generation_MissingMetadata_InitializesFreshIdentity()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var first = new TestHttpStore(time, backend); using var second = new TestHttpStore(time, backend);
        var before = await first.GetGenerationAsync("url"); Assert.Equal(before, await second.GetGenerationAsync("url"));
        backend.LoseMetadata(); Assert.NotEqual(before, await second.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task ClearMemory_RetiresNeverPreviouslyInvalidatedUrls()
    {
        using var storage = new TestHttpStore(new FakeTimeProvider());
        var before = await storage.GetGenerationAsync("url"); await storage.ClearAsync();
        Assert.NotEqual(before, await storage.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task UnconfirmedMutation_ThrowsTypedError_DisposesOriginResponse_AndNeverRetriesOrigin()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time); backend.FailAdvance = true;
        using var storage = new TestHttpStore(time, backend); using var origin = new SuccessfulWriteOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, storage, time));
        await Assert.ThrowsAsync<HttpCacheInvalidationException>(() => client.PostAsync("https://api.example.com/write", new StringContent("update")));
        Assert.True(origin.Written); Assert.True(origin.ResponseDisposed);
        backend.FailAdvance = false; time.Advance(TimeSpan.FromSeconds(11)); await storage.Core.FlushRecoveryAsync();
        Assert.Equal(0, storage.Core.PendingRecoveries);
    }

    [Fact]
    public async Task UnconfirmedRetirementOnAGet_ThrowsTheTypedHttpError_ThatSaysTheGetIsSafeToRetry()
    {
        // A GET whose answer replaces the stored response (no-store now) must retire it; that retirement is unconfirmed
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend); using var origin = new ReplacedRepresentationOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, storage, time));
        (await client.GetAsync("https://api.example.com/item")).Dispose();
        backend.FailAdvance = true;

        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/item");
        revalidate.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var error = await Assert.ThrowsAsync<HttpCacheInvalidationException>(() => client.SendAsync(revalidate));

        Assert.DoesNotContain("mutation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe to retry", error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.True(origin.ReplacementDisposed);
        backend.FailAdvance = false; time.Advance(TimeSpan.FromSeconds(11)); await storage.Core.FlushRecoveryAsync();
        Assert.Equal(0, storage.Core.PendingRecoveries);
    }

    [Fact]
    public async Task UnconfirmedAdminRemoval_KeepsTheCoreError_WithoutTalkingAboutAnOriginMutation()
    {
        // The admin endpoint answers 503 for this exception type; it is not an HTTP mutation
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend); using var origin = new ReplacedRepresentationOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new(), origin, storage, time));
        (await client.GetAsync("https://api.example.com/item")).Dispose();
        var stored = Assert.Single(storage.Core.List());
        backend.FailAdvance = true;

        var error = await Assert.ThrowsAnyAsync<Exception>(() => storage.Core.RemoveObservationAsync(stored.Id, CancellationToken.None).AsTask());

        Assert.IsType<CacheInvalidationQueuedException>(error);
        Assert.DoesNotContain("mutation", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Generations_DifferentCorePrefixes_RemainIsolated()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var first = new TestHttpStore(time, backend, prefix: "first:"); using var second = new TestHttpStore(time, backend, prefix: "second:");
        var untouched = await second.GetGenerationAsync("url"); await first.InvalidateAsync("url");
        Assert.NotEqual(untouched, await first.GetGenerationAsync("url")); Assert.Equal(untouched, await second.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task Cancellation_DuringOwnedAdvance_StopsOnlyCallerWait()
    {
        var time = new FakeTimeProvider(); var backend = HttpEpochTestCache.Create(time);
        using var storage = new TestHttpStore(time, backend); using var observer = new TestHttpStore(time, backend);
        var before = await observer.GetGenerationAsync("url"); backend.HoldNextAdvance = true;
        using var caller = new CancellationTokenSource(); var pending = storage.InvalidateAsync("url", caller.Token).AsTask();
        await backend.Started.Task; caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.StartsWith("unavailable:", await storage.GetGenerationAsync("url"));
        backend.Release.TrySetResult();
        // A second independent advance is serialized after the released one by the backend gate.
        await observer.InvalidateAsync("url");
        Assert.NotEqual(before, await observer.GetGenerationAsync("url")); Assert.Equal(2, backend.Advances);
    }

    private sealed class VersionedOrigin : HttpMessageHandler
    {
        public string Version { get; set; } = "old";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(Version),
                    Headers = { CacheControl = System.Net.Http.Headers.CacheControlHeaderValue.Parse("max-age=300") }
                }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
    }

    private sealed class SuccessfulWriteOrigin : HttpMessageHandler
    {
        public bool Written { get; private set; }
        public bool ResponseDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Written = true;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)
            {
                Content = new DisposalContent(() => ResponseDisposed = true)
            });
        }
    }

    /// <summary>Answers a cacheable response first, then one that may no longer be stored.</summary>
    private sealed class ReplacedRepresentationOrigin : HttpMessageHandler
    {
        private int _calls;
        public bool ReplacementDisposed { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                var cacheable = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("v1") };
                cacheable.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
                return Task.FromResult(cacheable);
            }

            var replacement = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new DisposalContent(() => ReplacementDisposed = true) };
            replacement.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(replacement);
        }
    }

    private sealed class DisposalContent(Action onDispose) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) onDispose();
        }
    }

}
