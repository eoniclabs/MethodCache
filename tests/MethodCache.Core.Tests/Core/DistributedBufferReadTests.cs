using System.Buffers;
using System.Runtime.InteropServices;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Core.Tests.Core;

public class DistributedBufferReadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1), InlineData(85_000), InlineData(1_048_576)]
    public async Task BufferRead_Deserialization_UsesTheBackendDestinationWithoutCopy(int payloadSize)
    {
        var cache = new TrackingBufferCache();
        var serializer = new BlobSerializer(payloadSize);
        var clock = new FakeTimeProvider();
        await using var layer = new DistributedCacheLayer(cache, serializer, new DistributedCacheOptions(), timeProvider: clock);
        await layer.WriteAsync("key", (byte)42, null, layer.Now, TimeSpan.FromMinutes(1));
        var read = await layer.TryGetAsync<byte>("key");
        Assert.True(read.Found);
        Assert.Equal((byte)42, read.Value);
        Assert.True(MemoryMarshal.TryGetArray(serializer.LastRead.First, out var payload));
        Assert.True(MemoryMarshal.TryGetArray(cache.LastDestination, out var destination));
        Assert.Same(destination.Array, payload.Array);
        Assert.Equal(payloadSize, serializer.LastRead.Length);
    }

    [Fact]
    public async Task ASecondRead_DoesNotOverwriteTheFirstReadsOwnedBuffer()
    {
        var cache = new TrackingBufferCache();
        var serializer = new BlobSerializer(1_048_576);
        var clock = new FakeTimeProvider();
        await using var layer = new DistributedCacheLayer(cache, serializer, new DistributedCacheOptions(), timeProvider: clock);
        await layer.WriteAsync("first", (byte)42, null, layer.Now, TimeSpan.FromMinutes(1));
        await layer.WriteAsync("second", (byte)7, null, layer.Now, TimeSpan.FromMinutes(1));
        Assert.True((await layer.TryGetAsync<byte>("first")).Found);
        var first = serializer.LastRead;
        Assert.True((await layer.TryGetAsync<byte>("second")).Found);
        Assert.Equal((byte)42, first.FirstSpan[0]);
        Assert.Equal((byte)7, serializer.LastRead.FirstSpan[0]);
    }

    [Fact]
    public async Task LargeBufferRead_AllocatesOnlyOneEntryBuffer()
    {
        const int size = 1_048_576;
        var cache = new TrackingBufferCache();
        var serializer = new BlobSerializer(size);
        await using var layer = new DistributedCacheLayer(cache, serializer, new DistributedCacheOptions(), timeProvider: new FakeTimeProvider());
        await layer.WriteAsync("key", (byte)42, null, layer.Now, TimeSpan.FromMinutes(1));
        await layer.TryGetAsync<byte>("key");
        var before = GC.GetAllocatedBytesForCurrentThread();
        var reading = layer.TryGetAsync<byte>("key");
        Assert.True(reading.IsCompletedSuccessfully);
        var result = await reading;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"One 1 MiB buffer read allocated {allocated:N0} bytes.");
        Assert.True(result.Found);
        Assert.InRange(allocated, size, size + 8192L);
    }

    [Fact]
    public async Task TimedOutRead_CompletingLate_DoesNotOverwriteAnotherReadsPayload()
    {
        var cache = new TrackingBufferCache();
        var serializer = new BlobSerializer(85_000);
        var clock = new FakeTimeProvider();
        var options = new DistributedCacheOptions { OperationTimeout = TimeSpan.FromSeconds(1) };
        await using var first = new DistributedCacheLayer(cache, serializer, options, timeProvider: clock);
        await first.WriteAsync("key", (byte)42, null, first.Now, TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cache.BeforeComplete = () => { entered.SetResult(); return release.Task; };
        cache.OnComplete = () => finished.SetResult();
        var pending = first.TryGetAsync<byte>("key").AsTask();
        await entered.Task;
        var abandonedBuffer = cache.LastDestination;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False((await pending).Found);
        Assert.Equal(0, serializer.ReadCalls);
        cache.BeforeComplete = null;
        cache.OnComplete = null;
        await using var second = new DistributedCacheLayer(cache, serializer, options, timeProvider: clock);
        await second.WriteAsync("key", (byte)7, null, second.Now, TimeSpan.FromMinutes(1));
        Assert.Equal((byte)7, (await second.TryGetAsync<byte>("key")).Value);
        var validPayload = serializer.LastRead;
        release.SetResult();
        await finished.Task;
        Assert.Equal((byte)42, abandonedBuffer.Span[^1]);
        Assert.Equal((byte)7, validPayload.FirstSpan[0]);
        Assert.Equal(1, serializer.ReadCalls);
    }

    private sealed class BlobSerializer(int size) : ISerializer
    {
        public string ContentType => "application/test-blob";
        public ReadOnlySequence<byte> LastRead;
        public int ReadCalls;
        public void Serialize<T>(T value, IBufferWriter<byte> destination)
        {
            destination.GetSpan(size)[..size].Fill((byte)(object)value!);
            destination.Advance(size);
        }
        public T? Deserialize<T>(ReadOnlySequence<byte> source)
        {
            LastRead = source;
            ReadCalls++;
            return (T)(object)source.FirstSpan[0];
        }
    }

    private sealed class TrackingBufferCache : IBufferDistributedCache, MethodCache.Extensibility.IAtomicInvalidationStore
    {
        private readonly IDistributedCache _inner = new MethodCache.TestSupport.AtomicTestCache();
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default) => ((MethodCache.Extensibility.IAtomicInvalidationStore)_inner).WriteInvalidationStampAsync(key, timestamp, retention, cancellationToken);
        public ReadOnlyMemory<byte> LastDestination;
        public Func<Task>? BeforeComplete;
        public Action? OnComplete;
        public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
        {
            var bytes = await _inner.GetAsync(key, token);
            if (bytes == null) return false;
            var memory = destination.GetMemory(bytes.Length);
            LastDestination = memory[..bytes.Length];
            var beforeComplete = BeforeComplete;
            var onComplete = OnComplete;
            if (beforeComplete != null) await beforeComplete();
            bytes.CopyTo(memory);
            destination.Advance(bytes.Length);
            onComplete?.Invoke();
            return true;
        }
        public bool TryGet(string key, IBufferWriter<byte> destination) => throw new NotSupportedException();
        public ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
            => new(_inner.SetAsync(key, value.ToArray(), options, token));
        public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public byte[]? Get(string key) => _inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => _inner.SetAsync(key, value, options, token);
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }
}
