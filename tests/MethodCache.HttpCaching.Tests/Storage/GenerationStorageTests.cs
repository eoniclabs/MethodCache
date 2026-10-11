using System.Collections.Concurrent;
using MethodCache.HttpCaching.Internal;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.HttpCaching.Tests.Storage;

public class GenerationStorageTests
{
    [Fact]
    public async Task Generation_SurvivesBackendDefaultExpiry_AndIsSharedAcrossInstances()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time);
        using var first = Store(time, backend);
        using var second = Store(time, backend);
        await first.InvalidateAsync("url");
        var generation = await second.GetGenerationAsync("url");
        time.Advance(TimeSpan.FromDays(365));
        Assert.Equal(generation, await second.GetGenerationAsync("url"));
        Assert.Equal(DateTimeOffset.MaxValue, Assert.Single(backend.Expirations.Values));
    }

    [Fact]
    public async Task Invalidation_CompletedOutOfOrder_NeverRestoresAnObservedGeneration()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time) { HoldNextWrite = true };
        using var first = Store(time, backend);
        using var second = Store(time, backend);
        var older = first.InvalidateAsync("url").AsTask();
        await backend.Started.Task;
        await second.InvalidateAsync("url");
        var newer = await second.GetGenerationAsync("url");
        backend.Release.TrySetResult();
        await older;
        var final = await second.GetGenerationAsync("url");
        Assert.NotEmpty(final);
        Assert.NotEqual(newer, final);
    }

    [Fact]
    public async Task Invalidation_IgnoredProviderWrite_RetriesWithAFreshGeneration()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time);
        using var storage = Store(time, backend);
        await storage.InvalidateAsync("url");
        var previous = await storage.GetGenerationAsync("url");
        backend.IgnoreNextWrite = true;
        await storage.InvalidateAsync("url");
        var current = await storage.GetGenerationAsync("url");
        Assert.NotEqual(previous, current);
        Assert.Equal(3, backend.WrittenGenerations.Count);
        Assert.NotEqual(backend.WrittenGenerations[1], backend.WrittenGenerations[2]);
    }

    [Fact]
    public async Task Invalidation_ProviderNeverConfirmsWrite_ReportsFailureAfterBoundedRetries()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time) { IgnoreEveryWrite = true };
        using var storage = Store(time, backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => storage.InvalidateAsync("url").AsTask());
        Assert.Equal(3, backend.WrittenGenerations.Count);
        Assert.Equal(3, backend.WrittenGenerations.Distinct().Count());
    }

    [Fact]
    public async Task Invalidation_ManyDistinctUrls_BoundsPermanentMetadata()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time);
        using var storage = Store(time, backend);
        for (var i = 0; i < 20_000; i++)
        {
            await storage.InvalidateAsync("url:" + i);
        }
        Assert.InRange(backend.Expirations.Count, 1, 4096);
    }

    [Fact]
    public async Task Generation_ReadFailureOrCorruption_NeverReusesTheInitialNamespace()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time) { ReadResult = [1, 2, 3] };
        using var storage = Store(time, backend);
        var corrupted = await storage.GetGenerationAsync("url");
        Assert.NotEmpty(corrupted);
        Assert.NotEqual(corrupted, await storage.GetGenerationAsync("url"));
        backend.ThrowOnRead = true;
        var failed = await storage.GetGenerationAsync("url");
        Assert.NotEmpty(failed);
        Assert.NotEqual(failed, await storage.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task ClearMemory_ChangesGenerations_EvenForUrlsNeverInvalidated()
    {
        using var storage = Store(new FakeTimeProvider());
        var old = await storage.GetGenerationAsync("url");
        await storage.ClearAsync();
        Assert.NotEqual(old, await storage.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task InvalidationWriteFailure_Propagates_AndDisposesTheOriginResponse()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time) { IgnoreEveryWrite = true };
        using var storage = Store(time, backend);
        using var origin = new SuccessfulWriteOrigin();
        using var client = new HttpClient(HttpCacheTestFactory.CreateHandler(new HttpCacheOptions(), origin, storage, time));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("https://api.example.com/write", new StringContent("update")));
        Assert.True(origin.Written);
        Assert.True(origin.ResponseDisposed);
    }

    [Fact]
    public async Task Generations_DifferentStoragePrefixes_RemainIsolated()
    {
        var time = new FakeTimeProvider();
        var backend = new EpochBackend(time);
        using var first = new HttpCacheStore(Options.Create(new HttpCacheStorageOptions { KeyPrefix = "first:" }), NullLogger<HttpCacheStore>.Instance, time, backend);
        using var second = new HttpCacheStore(Options.Create(new HttpCacheStorageOptions { KeyPrefix = "second:" }), NullLogger<HttpCacheStore>.Instance, time, backend);
        await first.InvalidateAsync("url");
        Assert.NotEmpty(await first.GetGenerationAsync("url"));
        Assert.Empty(await second.GetGenerationAsync("url"));
    }

    [Fact]
    public async Task GenerationOperations_Cancelled_DoNotReadOrMutateMemory()
    {
        using var storage = Store(new FakeTimeProvider());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.GetGenerationAsync("url", cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.InvalidateAsync("url", cancellation.Token).AsTask());
        Assert.Empty(await storage.GetGenerationAsync("url"));
    }

    private static HttpCacheStore Store(TimeProvider time, IDistributedCache? distributed = null)
        => new(Options.Create(new HttpCacheStorageOptions()), NullLogger<HttpCacheStore>.Instance, time, distributed);

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

    private sealed class DisposalContent(Action onDispose) : ByteArrayContent([])
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) onDispose();
        }
    }

    private sealed class EpochBackend(TimeProvider time) : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _values = new();
        public ConcurrentDictionary<string, DateTimeOffset> Expirations { get; } = new();
        public bool HoldNextWrite { get; set; }
        public bool IgnoreNextWrite { get; set; }
        public bool IgnoreEveryWrite { get; set; }
        public List<string> WrittenGenerations { get; } = new();
        public bool ThrowOnRead { get; set; }
        public byte[]? ReadResult { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[]? Get(string key)
        {
            if (ThrowOnRead) throw new InvalidOperationException("Backend unavailable");
            if (ReadResult != null) return ReadResult;
            return Expirations.TryGetValue(key, out var expires) && time.GetUtcNow() < expires ? _values[key] : null;
        }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            WrittenGenerations.Add(Convert.ToHexString(value));
            if (IgnoreEveryWrite || IgnoreNextWrite)
            {
                IgnoreNextWrite = false;
                return;
            }
            _values[key] = value;
            Expirations[key] = options.AbsoluteExpiration ?? time.GetUtcNow() + (options.AbsoluteExpirationRelativeToNow ?? TimeSpan.FromMinutes(20));
        }
        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            if (HoldNextWrite)
            {
                HoldNextWrite = false;
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            Set(key, value, options);
        }
        public void Remove(string key) { _values.TryRemove(key, out _); Expirations.TryRemove(key, out _); }
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
    }
}
