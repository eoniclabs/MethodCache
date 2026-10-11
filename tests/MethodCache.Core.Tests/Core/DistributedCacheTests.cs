using FluentAssertions;
using MethodCache.Core.Configuration;
using MethodCache.Core.Infrastructure;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Core.Infrastructure.Serialization;
using MethodCache.Core.Runtime;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Runtime.Execution;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Serialization.MessagePack;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using MemoryCacheOptions = MethodCache.Core.Configuration.MemoryCacheOptions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Two cache managers sharing one distributed cache stand in for two application instances.
/// </summary>
public class DistributedCacheTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    // MemoryDistributedCache only handles byte arrays; BufferCache covers IBufferDistributedCache
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly IDistributedCache _shared = new MemoryDistributedCache(MsOptions.Create(new MemoryDistributedCacheOptions()));
    private readonly TestBackplaneHub _hub = new();

    private InMemoryCacheManager CreateNode(bool backplane = true, IDistributedCache? cache = null, Action<DistributedCacheOptions>? configure = null, ISerializer? serializer = null, TimeProvider? time = null)
    {
        // The nodes share one clock, so only the clock skew tests need a tolerance
        var options = new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero };
        configure?.Invoke(options);

        var layer = new DistributedCacheLayer(
            cache ?? _shared,
            serializer ?? new JsonCacheSerializer(),
            options,
            backplane ? _hub.Join() : null,
            time ?? _time);

        return new InMemoryCacheManager(
            new NullCacheMetricsProvider(),
            MsOptions.Create(new MemoryCacheOptions { EnableBackgroundCleanup = false }),
            time ?? _time,
            layer);
    }

    private static CacheRuntimePolicy Policy(params string[] tags) =>
        CacheRuntimePolicy.Empty("M") with { Duration = OneMinute, Tags = tags };

    private static Task<string> Get(ICacheManager node, string key, Func<string> value, params string[] tags) =>
        node.GetOrCreateFastAsync(key, "M", () => Task.FromResult(value()), Policy(tags));

    [Fact]
    public void BlockingOnAMiss_NeverNeedsTheCallersSynchronizationContext()
    {
        // Generated decorators for synchronous methods block on the load. On a UI or classic ASP.NET thread that
        // only deadlocks if a continuation needs the blocked thread's context, so none may post to it.
        var node = CreateNode(backplane: false, cache: new SlowDistributedCache(_shared));
        var context = new RecordingSynchronizationContext();
        string? result = null;

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            result = Get(node, "key", () => "loaded").GetAwaiter().GetResult();
        });
        thread.Start();

        thread.Join(TimeSpan.FromSeconds(10)).Should().BeTrue();
        result.Should().Be("loaded");
        context.Posts.Should().Be(0);
    }

    [Fact]
    public async Task EntriesWrittenByAnotherSerializer_AreMisses()
    {
        var messagePack = CreateNode(backplane: false, serializer: new MessagePackCacheSerializer());
        var json = CreateNode(backplane: false);

        // JSON's "42" is also valid MessagePack, so without the stored content type this would read a wrong number
        await json.GetOrCreateFastAsync("key", "M", () => Task.FromResult(42), Policy());
        var result = await messagePack.GetOrCreateFastAsync("key", "M", () => Task.FromResult(7), Policy());

        result.Should().Be(7);
    }

    [Fact]
    public async Task CacheWhen_RejectedResults_AreNotWrittenToTheDistributedCache()
    {
        var first = CreateNode();
        var second = CreateNode();
        var policy = Policy() with { CacheWhen = v => v is not "rejected" };

        await first.GetOrCreateFastAsync("key", "M", () => Task.FromResult("rejected"), policy);
        var result = await second.GetOrCreateFastAsync("key", "M", () => Task.FromResult("from second"), policy);

        result.Should().Be("from second");
    }

    [Fact]
    public async Task SecondInstance_ReadsTheValueTheFirstStored()
    {
        var first = CreateNode();
        var second = CreateNode();

        await Get(first, "key", () => "from first");
        var result = await Get(second, "key", () => "from second");

        result.Should().Be("from first");
    }

    [Fact]
    public async Task SecondInstance_ReadsTheValue_ThroughABufferDistributedCache()
    {
        var bufferCache = new BufferCache(_shared);
        var first = CreateNode(cache: bufferCache);
        var second = CreateNode(cache: bufferCache);

        await Get(first, "key", () => "from first", "users");
        var result = await Get(second, "key", () => "from second", "users");

        result.Should().Be("from first");
        bufferCache.BufferReads.Should().BePositive();
    }

    [Fact]
    public async Task NullResults_AreSharedToo()
    {
        var first = CreateNode();
        var second = CreateNode();
        var secondCalls = 0;

        await first.GetOrCreateFastAsync<string?>("key", "M", () => Task.FromResult<string?>(null), Policy());
        var result = await second.GetOrCreateFastAsync<string?>("key", "M", () =>
        {
            secondCalls++;
            return Task.FromResult<string?>("loaded");
        }, Policy());

        result.Should().BeNull();
        secondCalls.Should().Be(0);
    }

    [Fact]
    public async Task TagInvalidation_ReachesEveryInstance()
    {
        var first = CreateNode();
        var second = CreateNode();
        await Get(first, "key", () => "v1", "users");
        await Get(second, "key", () => "unused", "users");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await first.InvalidateByTagsAsync("users");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await Get(second, "key", () => "v2", "users")).Should().Be("v2");
        (await Get(first, "key", () => "v3", "users")).Should().Be("v2");
    }

    [Fact]
    public async Task TagInvalidation_WithoutBackplane_StillMakesTheDistributedEntryStale()
    {
        var first = CreateNode(backplane: false, configure: o => o.TagCheckInterval = TimeSpan.Zero);
        var second = CreateNode(backplane: false, configure: o => o.TagCheckInterval = TimeSpan.Zero);
        await Get(first, "key", () => "v1", "users");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await first.InvalidateByTagsAsync("users");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await Get(second, "key", () => "v2", "users")).Should().Be("v2");
    }

    [Fact]
    public async Task TagInvalidatedWhileLoading_ValueIsNotReusedByOtherInstances()
    {
        var first = CreateNode();
        var second = CreateNode();
        var loading = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var load = first.GetOrCreateFastAsync("key", "M", async () =>
        {
            loading.SetResult();
            await release.Task;
            return "read before the invalidation";
        }, Policy("users"));

        await loading.Task;
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await second.InvalidateByTagsAsync("users");
        _time.Advance(TimeSpan.FromMilliseconds(1));
        release.SetResult();
        await load;

        (await Get(second, "key", () => "fresh", "users")).Should().Be("fresh");
    }

    [Fact]
    public async Task KeyInvalidation_RemovesTheEntryEverywhere()
    {
        var first = CreateNode();
        var second = CreateNode();
        await Get(first, "key", () => "v1");
        await Get(second, "key", () => "unused");

        await first.InvalidateByKeysAsync("key");

        (await Get(second, "key", () => "v2")).Should().Be("v2");
    }

    [Fact]
    public async Task Write_DropsOtherInstancesInMemoryCopies()
    {
        var first = CreateNode();
        var second = CreateNode();
        await Get(first, "key", () => "v1");
        await Get(second, "key", () => "unused");

        await first.SetAsync("key", "v2", OneMinute);

        (await Get(second, "key", () => "unused")).Should().Be("v2");
    }

    [Fact]
    public async Task Clear_ClearsEveryInstance()
    {
        var first = CreateNode();
        var second = CreateNode();
        await Get(first, "key", () => "v1");
        await Get(second, "key", () => "unused");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await first.ClearAsync();
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await Get(second, "key", () => "v2")).Should().Be("v2");
    }

    [Fact]
    public async Task DistributedCacheFailure_FallsBackToTheFactory()
    {
        var node = CreateNode(backplane: false, cache: new FailingDistributedCache());

        (await Get(node, "key", () => "loaded")).Should().Be("loaded");
        (await Get(node, "key", () => "unused")).Should().Be("loaded");
    }

    [Fact]
    public async Task TagPatternInvalidation_IsNotSupported()
    {
        var node = CreateNode();

        var act = () => node.InvalidateByTagPatternAsync("user:*");

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task EntriesExpireInTheDistributedCache()
    {
        var first = CreateNode();
        var second = CreateNode();
        await first.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v1"),
            CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromSeconds(10) });

        _time.Advance(TimeSpan.FromSeconds(11));

        (await Get(second, "key", () => "v2")).Should().Be("v2");
    }

    [Fact]
    public async Task AddMethodCacheDistributedCache_StoresMethodResultsInTheRegisteredDistributedCache()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(options => options.KeyPrefix = "app:");

        await using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<ICacheManager>();
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("value"), Policy());

        var stored = await provider.GetRequiredService<IDistributedCache>().GetAsync("app:e:key");
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task HangingDistributedCache_TimesOut_AndLaterCallsSkipIt()
    {
        var node = CreateNode(backplane: false, cache: new HangingDistributedCache());

        var first = Get(node, "key", () => "loaded");
        first.IsCompleted.Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));
        (await first).Should().Be("loaded");

        // The breaker is open, so the next miss doesn't wait for the cache at all
        var second = Get(node, "other", () => "loaded too");
        second.IsCompletedSuccessfully.Should().BeTrue();
        (await second).Should().Be("loaded too");
    }

    [Fact]
    public async Task CircuitBreaker_SkipsTheCacheWhileOpen_AndUsesItAgainOnceItRecovers()
    {
        var cache = new ToggleDistributedCache(_shared) { Down = true };
        var node = CreateNode(backplane: false, cache: cache);
        var other = CreateNode(backplane: false);

        (await Get(node, "a", () => "a1")).Should().Be("a1");
        var callsWhenTripped = cache.Calls;
        (await Get(node, "b", () => "b1")).Should().Be("b1");
        cache.Calls.Should().Be(callsWhenTripped);

        cache.Down = false;
        _time.Advance(TimeSpan.FromSeconds(10));

        (await Get(node, "c", () => "c1")).Should().Be("c1");
        cache.Calls.Should().BeGreaterThan(callsWhenTripped);
        (await Get(other, "c", () => "unused")).Should().Be("c1");
    }

    [Fact]
    public async Task InvalidationDuringAnOutage_NeverThrows_AndIsStoredOnceTheCacheIsBack()
    {
        var cache = new ToggleDistributedCache(_shared);
        var node = CreateNode(backplane: false, cache: cache);
        var other = CreateNode(backplane: false, configure: o => o.TagCheckInterval = TimeSpan.Zero);
        await Get(node, "key", () => "v1");
        await Get(node, "tagged", () => "t1", "users");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        cache.Down = true;
        await node.InvalidateByKeysAsync("key");
        await node.InvalidateByTagsAsync("users");

        // While the invalidation is queued this node neither reads nor writes the distributed cache
        cache.Down = false;
        (await Get(node, "key", () => "local")).Should().Be("local");

        _time.Advance(TimeSpan.FromSeconds(10));

        (await Get(other, "key", () => "v2")).Should().Be("v2");
        (await Get(other, "tagged", () => "t2", "users")).Should().Be("t2");
        await Get(node, "shared again", () => "s1");
        (await Get(other, "shared again", () => "unused")).Should().Be("s1");
    }

    [Fact]
    public async Task LoadThatStartedBeforeARemoval_DoesNotWriteItsValueBack()
    {
        var first = CreateNode(backplane: false);
        var second = CreateNode(backplane: false);
        var third = CreateNode(backplane: false);
        var release = new TaskCompletionSource<string>();

        var load = first.GetOrCreateFastAsync("key", "M", () => release.Task, Policy());
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await second.InvalidateByKeysAsync("key");
        release.SetResult("loaded before the removal");
        await load;

        (await Get(third, "key", () => "fresh")).Should().Be("fresh");
    }

    [Theory]
    [InlineData(2, "fresh")]
    [InlineData(0, "stale")]
    public async Task ClockSkewTolerance_CoversAServerWhoseClockRunsAhead(int toleranceSeconds, string expected)
    {
        void Configure(DistributedCacheOptions o)
        {
            o.TagCheckInterval = TimeSpan.Zero;
            o.ClockSkewTolerance = TimeSpan.FromSeconds(toleranceSeconds);
        }

        var ahead = new FakeTimeProvider(_time.GetUtcNow() + TimeSpan.FromSeconds(1));
        var fast = CreateNode(backplane: false, configure: Configure, time: ahead);
        var accurate = CreateNode(backplane: false, configure: Configure);
        var reader = CreateNode(backplane: false, configure: Configure);

        // Loaded before the invalidation, but stamped later by the fast clock
        await Get(fast, "key", () => "stale", "users");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        await accurate.InvalidateByTagsAsync("users");

        (await Get(reader, "key", () => "fresh", "users")).Should().Be(expected);
    }

    [Fact]
    public void Options_RejectInvalidResilienceSettings()
    {
        var options = new DistributedCacheOptions();

        ((Action)(() => options.OperationTimeout = TimeSpan.Zero)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => options.CircuitBreakerDuration = TimeSpan.Zero)).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => options.ClockSkewTolerance = TimeSpan.FromSeconds(-1))).Should().Throw<ArgumentOutOfRangeException>();
        options.ClockSkewTolerance = TimeSpan.Zero;
    }

    private sealed class TestBackplaneHub
    {
        private readonly List<TestBackplane> _members = new();

        public IBackplane Join()
        {
            var member = new TestBackplane(this);
            _members.Add(member);
            return member;
        }

        public async Task SendAsync(BackplaneMessage message)
        {
            foreach (var member in _members)
            {
                if (member.Handler != null)
                {
                    await member.Handler(message);
                }
            }
        }
    }

    private sealed class TestBackplane(TestBackplaneHub hub) : IBackplane
    {
        public Func<BackplaneMessage, Task>? Handler { get; private set; }

        public string InstanceId { get; } = Guid.NewGuid().ToString("N");

        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) =>
            hub.SendAsync(new BackplaneMessage { Type = BackplaneMessageType.KeyInvalidation, Key = key, InstanceId = InstanceId });

        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) =>
            hub.SendAsync(new BackplaneMessage { Type = BackplaneMessageType.TagInvalidation, Tag = tag, InstanceId = InstanceId });

        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
        {
            Handler = onMessage;
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(CancellationToken cancellationToken = default)
        {
            Handler = null;
            return Task.CompletedTask;
        }
    }

    private sealed class BufferCache(IDistributedCache inner) : IBufferDistributedCache
    {
        public int BufferReads { get; private set; }

        public async ValueTask<bool> TryGetAsync(string key, System.Buffers.IBufferWriter<byte> destination, CancellationToken token = default)
        {
            BufferReads++;
            var bytes = await inner.GetAsync(key, token);
            if (bytes == null)
            {
                return false;
            }

            System.Buffers.BuffersExtensions.Write(destination, bytes);
            return true;
        }

        public bool TryGet(string key, System.Buffers.IBufferWriter<byte> destination) => throw new NotSupportedException();

        public ValueTask SetAsync(string key, System.Buffers.ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            new(inner.SetAsync(key, System.Buffers.BuffersExtensions.ToArray(value), options, token));

        public void Set(string key, System.Buffers.ReadOnlySequence<byte> value, DistributedCacheEntryOptions options) => throw new NotSupportedException();

        public byte[]? Get(string key) => inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => inner.GetAsync(key, token);
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) => inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => inner.RemoveAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => inner.SetAsync(key, value, options, token);
    }

    private sealed class FailingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new InvalidOperationException("down");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("down");
        public void Refresh(string key) => throw new InvalidOperationException("down");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("down");
        public void Remove(string key) => throw new InvalidOperationException("down");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new InvalidOperationException("down");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new InvalidOperationException("down");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new InvalidOperationException("down");
    }

    /// <summary>Never answers and ignores cancellation, like a server that stopped responding.</summary>
    private sealed class HangingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new NotSupportedException();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => new TaskCompletionSource<byte[]?>().Task;
        public void Refresh(string key) => throw new NotSupportedException();
        public Task RefreshAsync(string key, CancellationToken token = default) => new TaskCompletionSource().Task;
        public void Remove(string key) => throw new NotSupportedException();
        public Task RemoveAsync(string key, CancellationToken token = default) => new TaskCompletionSource().Task;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => new TaskCompletionSource().Task;
    }

    /// <summary>Fails every call while <see cref="Down"/> is set, and counts the calls that reach it.</summary>
    private sealed class ToggleDistributedCache(IDistributedCache inner) : IDistributedCache
    {
        public bool Down { get; set; }

        public int Calls { get; private set; }

        public byte[]? Get(string key) => throw new NotSupportedException();
        public void Refresh(string key) => throw new NotSupportedException();
        public void Remove(string key) => throw new NotSupportedException();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new NotSupportedException();

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Run(() => inner.GetAsync(key, token));
        public Task RefreshAsync(string key, CancellationToken token = default) => Run(() => inner.RefreshAsync(key, token));
        public Task RemoveAsync(string key, CancellationToken token = default) => Run(() => inner.RemoveAsync(key, token));
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => Run(() => inner.SetAsync(key, value, options, token));

        private Task<TResult> Run<TResult>(Func<Task<TResult>> call)
        {
            Calls++;
            return Down ? Task.FromException<TResult>(new InvalidOperationException("down")) : call();
        }

        private Task Run(Func<Task> call)
        {
            Calls++;
            return Down ? Task.FromException(new InvalidOperationException("down")) : call();
        }
    }

    /// <summary>Completes every call asynchronously, like a real network cache.</summary>
    private sealed class SlowDistributedCache(IDistributedCache inner) : IDistributedCache
    {
        public byte[]? Get(string key) => inner.Get(key);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => inner.Set(key, value, options);
        public void Refresh(string key) => inner.Refresh(key);
        public void Remove(string key) => inner.Remove(key);

        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            await Task.Delay(10, token).ConfigureAwait(false);
            return inner.Get(key);
        }

        public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            await Task.Delay(10, token).ConfigureAwait(false);
            inner.Set(key, value, options);
        }

        public async Task RefreshAsync(string key, CancellationToken token = default)
        {
            await Task.Delay(10, token).ConfigureAwait(false);
            inner.Refresh(key);
        }

        public async Task RemoveAsync(string key, CancellationToken token = default)
        {
            await Task.Delay(10, token).ConfigureAwait(false);
            inner.Remove(key);
        }
    }

    /// <summary>Runs posted work on the thread pool so a test can't hang, and counts it.</summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}
