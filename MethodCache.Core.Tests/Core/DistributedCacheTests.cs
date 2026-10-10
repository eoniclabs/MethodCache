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

    private InMemoryCacheManager CreateNode(bool backplane = true, IDistributedCache? cache = null, Action<DistributedCacheOptions>? configure = null)
    {
        var options = new DistributedCacheOptions();
        configure?.Invoke(options);

        var layer = new DistributedCacheLayer(
            cache ?? _shared,
            new MessagePackSerializer(),
            options,
            backplane ? _hub.Join() : null,
            _time);

        return new InMemoryCacheManager(
            new NullCacheMetricsProvider(),
            MsOptions.Create(new MemoryCacheOptions { EnableBackgroundCleanup = false }),
            _time,
            layer);
    }

    private static CacheRuntimePolicy Policy(params string[] tags) =>
        CacheRuntimePolicy.Empty("M") with { Duration = OneMinute, Tags = tags };

    private static Task<string> Get(ICacheManager node, string key, Func<string> value, params string[] tags) =>
        node.GetOrCreateFastAsync(key, "M", () => Task.FromResult(value()), Policy(tags));

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
}
