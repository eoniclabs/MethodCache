using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Serialization.MessagePack;
using MessagePack;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Pins the distributed cache threats in docs/security.md: whatever bytes sit in Redis or SQL Server, reading them
/// is a miss or a value of the declared type, never an exception or a huge allocation; serializers don't create
/// types named by the data; and a tag, whatever its text, never clears more than the entries carrying it.
/// </summary>
public class DistributedCacheSecurityTests
{
    private const string EntryPrefix = "methodcache:e6:s1:";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IDistributedCache _shared;

    public DistributedCacheSecurityTests() => _shared = new MethodCache.TestSupport.AtomicTestCache(_time);
    private readonly Hub _hub = new();

    private InMemoryCacheManager CreateNode(ISerializer? serializer = null) => new(
                MsOptions.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }),
        _time,
        new DistributedCacheLayer(_shared, serializer ?? new JsonCacheSerializer(), new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero }, _hub.Join(), _time));

    private static CacheRuntimePolicy Policy(params string[] tags) =>
        CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1), Tags = tags };

    [Theory]
    [InlineData(4, 0)]
    [InlineData(6, 1)]
    [InlineData(6, -1)]
    [InlineData(6, int.MaxValue)]
    public async Task ObsoleteVersionOrInvalidUtf16Length_IsAMiss(int version, int length)
    {
        var writer = CreateNode();
        await writer.GetOrCreateFastAsync("k", "M", () => Task.FromResult("stored"), Policy("users"));
        var stored = (await _shared.GetAsync(EntryPrefix + "k"))!;
        stored[0] = (byte)version;
        BinaryPrimitives.WriteInt32LittleEndian(stored.AsSpan(44), length);
        await _shared.SetAsync(EntryPrefix + "k", stored);
        var reader = CreateNode();
        Assert.Equal("loaded", await reader.GetOrCreateFastAsync("k", "M", () => Task.FromResult("loaded"), Policy("users")));
    }

    [Fact]
    public async Task RandomBytesInTheDistributedCache_AreMisses()
    {
        var node = CreateNode();
        var random = new Random(1234);

        for (var i = 0; i < 500; i++)
        {
            var garbage = new byte[random.Next(0, 64)];
            random.NextBytes(garbage);
            if (garbage.Length > 0 && random.Next(2) == 0)
            {
                garbage[0] = 5; // the current format version, so the header is parsed further
            }

            await _shared.SetAsync(EntryPrefix + "k" + i, garbage);
            (await node.GetOrCreateFastAsync("k" + i, "M", () => Task.FromResult("loaded"), Policy())).Should().Be("loaded");
        }
    }

    [Fact]
    public async Task EveryTruncationOfAStoredEntry_IsAMiss()
    {
        var writer = CreateNode();
        await writer.GetOrCreateFastAsync("k", "M", () => Task.FromResult("stored value"), Policy("users"));
        var stored = await _shared.GetAsync(EntryPrefix + "k");
        stored.Should().NotBeNull();

        for (var length = 0; length < stored!.Length; length++)
        {
            await _shared.SetAsync(EntryPrefix + "k", stored[..length]);
            var reader = CreateNode();
            (await reader.GetOrCreateFastAsync("k", "M", () => Task.FromResult("loaded"), Policy("users"))).Should().Be("loaded", $"truncated to {length} bytes");
        }
    }

    [Fact]
    public async Task AHugeTagCount_IsAMiss_WithoutAllocatingIt()
    {
        var header = new byte[1 + 1 + 8 + 8 + 8 + 4];
        header[0] = 5;                                                       // format version
        header[1] = 1;                                                       // a value
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(2), _time.GetUtcNow().ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(10), _time.GetUtcNow().ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(18), _time.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(26), int.MaxValue); // tag count
        await _shared.SetAsync(EntryPrefix + "k", header.Concat(new byte[64]).ToArray());
        var node = CreateNode();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = await node.GetOrCreateFastAsync("k", "M", () => Task.FromResult("loaded"), Policy());

        result.Should().Be("loaded");
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1024 * 1024);
    }

    [Fact]
    public async Task JsonPayload_NamingAType_IsReadAsTheDeclaredType()
    {
        // System.Text.Json never creates the type a "$type" property names unless the declared type opts in
        var writer = CreateNode();
        await writer.GetOrCreateFastAsync("k", "M", () => Task.FromResult(new Profile { Name = "Ada" }), Policy());
        var stored = (await _shared.GetAsync(EntryPrefix + "k"))!;
        var json = stored.AsSpan().IndexOf("{\"Name\""u8);
        json.Should().BeGreaterThan(0);
        var tampered = stored[..json]
            .Concat(Encoding.UTF8.GetBytes("{\"$type\":\"System.IO.FileInfo, System.IO.FileSystem\",\"FullName\":\"/etc/passwd\","))
            .Concat(stored[(json + 1)..])
            .ToArray();
        await _shared.SetAsync(EntryPrefix + "k", tampered);

        var value = await CreateNode().GetOrCreateFastAsync<object>("k", "M", () => Task.FromResult<object>("loaded"), Policy());

        value.Should().NotBeOfType<FileInfo>();
    }

    [Fact]
    public void MessagePackSerializer_TreatsStoredDataAsUntrusted_AndCreatesNoNamedTypes()
    {
        var serializer = new MessagePackCacheSerializer();
        var options = (MessagePackSerializerOptions)typeof(MessagePackCacheSerializer)
            .GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(serializer)!;
        options.Security.HashCollisionResistant.Should().BeTrue();
        options.Security.MaximumObjectGraphDepth.Should().Be(MessagePackSecurity.UntrustedData.MaximumObjectGraphDepth);

        // A payload written by the typeless serializer names its type; it is refused, which the cache reads as a miss
        var typeless = MessagePackSerializer.Typeless.Serialize(new Profile { Name = "Ada" });
        var read = () => serializer.Deserialize<object>(new System.Buffers.ReadOnlySequence<byte>(typeless));

        read.Should().Throw<MessagePackSerializationException>();
    }

    [Fact]
    public async Task ATagThatLooksLikeTheInternalClearTag_OnlyInvalidatesItsOwnEntries()
    {
        // Invalidating everything is ClearAsync's job; a tag built from user text must not reach it
        var first = CreateNode();
        var second = CreateNode();
        await first.GetOrCreateFastAsync("users", "M", () => Task.FromResult("users v1"), Policy("users"));
        await first.GetOrCreateFastAsync("odd", "M", () => Task.FromResult("odd v1"), Policy(DistributedCacheLayer.ClearTag));
        await second.GetOrCreateFastAsync("users", "M", () => Task.FromResult("unused"), Policy("users"));
        await second.GetOrCreateFastAsync("odd", "M", () => Task.FromResult("unused"), Policy(DistributedCacheLayer.ClearTag));
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await first.InvalidateByTagsAsync(DistributedCacheLayer.ClearTag);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await second.GetOrCreateFastAsync("users", "M", () => Task.FromResult("users v2"), Policy("users"))).Should().Be("users v1");
        (await CreateNode().GetOrCreateFastAsync("users", "M", () => Task.FromResult("users v2"), Policy("users"))).Should().Be("users v1");
        (await second.GetOrCreateFastAsync("odd", "M", () => Task.FromResult("odd v2"), Policy(DistributedCacheLayer.ClearTag))).Should().Be("odd v2");
        (await CreateNode().GetOrCreateFastAsync("odd", "M", () => Task.FromResult("odd v3"), Policy(DistributedCacheLayer.ClearTag))).Should().Be("odd v2");
    }

    [Theory]
    [InlineData("e:k")]
    [InlineData("methodcache:e6:s1:k")]
    [InlineData("r:k")]
    [InlineData("k")]
    public async Task ATagNamedLikeAStorageKey_DoesNotTouchThatEntry(string tag)
    {
        // Entries, tag stamps and removal stamps live under different prefixes, so no tag text reaches an entry
        var first = CreateNode();
        await first.GetOrCreateFastAsync("k", "M", () => Task.FromResult("v1"), Policy("users"));
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await first.InvalidateByTagsAsync(tag);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        (await CreateNode().GetOrCreateFastAsync("k", "M", () => Task.FromResult("v2"), Policy("users"))).Should().Be("v1");
    }

    public sealed class Profile
    {
        public string? Name { get; set; }
    }

    private sealed class Hub
    {
        private readonly List<Member> _members = new();

        public IBackplane Join()
        {
            var member = new Member(this);
            _members.Add(member);
            return member;
        }

        private async Task SendAsync(BackplaneMessage message)
        {
            foreach (var member in _members.ToArray())
            {
                if (member.Handler != null)
                {
                    await member.Handler(message);
                }
            }
        }

        private sealed class Member(Hub hub) : IBackplane
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
    }
}
