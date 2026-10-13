using System.Buffers;
using FluentAssertions;
using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Serialization.MessagePack;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// Values shared through the distributed cache must come back as the method returned them: what the serializer
/// carries is shared, and a method whose values it can't read back unchanged stays in memory only.
/// </summary>
public class DistributedSerializationTests
{
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly IDistributedCache _shared = new MethodCache.TestSupport.AtomicTestCache();
    private readonly RecordingLogger _logger = new();

    public class Entity
    {
        public int Id { get; private set; }

        public static Entity Create(int id) => new() { Id = id };
    }

    public struct Point
    {
        public int X;
        public int Y;
    }

    public class Animal
    {
        public string Name { get; set; } = "";
    }

    public class Dog : Animal
    {
        public string Breed { get; set; } = "";
    }

    public static TheoryData<ISerializer> Serializers => new()
    {
        new JsonCacheSerializer(),
        new MessagePackCacheSerializer(),
    };

    private (InMemoryCacheManager Node, DistributedCacheLayer Layer) CreateNode(ISerializer? serializer = null)
    {
        var layer = new DistributedCacheLayer(
            _shared,
            serializer ?? new JsonCacheSerializer(),
            new DistributedCacheOptions { ClockSkewTolerance = TimeSpan.Zero },
            backplane: null,
            _time,
            _logger);
        var node = new InMemoryCacheManager(
            new NullCacheMetricsProvider(),
            MsOptions.Create(new MethodCacheMemoryOptions { EnableBackgroundCleanup = false }),
            _time,
            layer);
        return (node, layer);
    }

    private static CacheRuntimePolicy Policy(string method = "M") => CacheRuntimePolicy.Empty(method) with { Duration = OneMinute };

    private static Task<T> Load<T>(ICacheManager node, string key, Func<T> value, string method = "M")
        => node.GetOrCreateFastAsync(key, method, () => Task.FromResult(value()), Policy(method));

    private static T? RoundTrip<T>(ISerializer serializer, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(value, buffer);
        return serializer.Deserialize<T>(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }

    private int Warnings => _logger.Messages.Count(m => m.Level == LogLevel.Warning && m.Message.Contains("in memory only", StringComparison.Ordinal));

    // ---------- Values the serializers carry ----------

    [Theory]
    [MemberData(nameof(Serializers))]
    public void Serializer_ValueTuple_RoundTrips(ISerializer serializer)
        => RoundTrip(serializer, (Total: 7, Name: "x")).Should().Be((7, "x"));

    [Theory]
    [MemberData(nameof(Serializers))]
    public void Serializer_StructWithPublicFields_RoundTrips(ISerializer serializer)
        => RoundTrip(serializer, new Point { X = 1, Y = 2 }).Should().Be(new Point { X = 1, Y = 2 });

    [Fact]
    public async Task L2Hit_OfAValueTuple_EqualsTheValueTheMethodReturned()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        (await Load(a, "k", () => (Total: 7, Name: "x"))).Should().Be((7, "x"));

        var fromL2 = await b.GetOrCreateFastAsync<(int Total, string Name)>(
            "k", "M", () => throw new InvalidOperationException("B must read A's value from L2"), Policy());
        fromL2.Should().Be((7, "x"), "an L2 hit must return what the method returned on the other node");
        Warnings.Should().Be(0);
    }

    // ---------- Values they can't carry stay in memory ----------

    [Fact]
    public async Task EntityWithPrivateSetter_StaysInMemoryOnly_SoNoNodeReadsItBackWithoutItsId()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        (await Load(a, "k", () => Entity.Create(42))).Id.Should().Be(42);
        (await Load(a, "k", () => Entity.Create(0))).Id.Should().Be(42, "the memory hit keeps the value");
        await Load(a, "other", () => Entity.Create(7));
        Warnings.Should().Be(1, "one warning for the method, however often it runs");

        // B never sees a copy without the id: there is none in the distributed cache, so B runs its method
        (await Load(b, "k", () => Entity.Create(42))).Id.Should().Be(42);
        _shared.Get("methodcache:e:k").Should().BeNull();
        Warnings.Should().Be(2, "each instance warns once");
    }

    [Fact]
    public async Task DerivedValueOfADeclaredBaseType_StaysInMemoryOnly()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        (await Load<Animal>(a, "k", () => new Dog { Name = "a", Breed = "b" })).Should().BeOfType<Dog>();

        (await Load<Animal>(b, "k", () => new Dog { Name = "a", Breed = "b" })).Should().BeOfType<Dog>("B ran its method instead of reading a plain Animal");
        _shared.Get("methodcache:e:k").Should().BeNull();
        Warnings.Should().Be(2, "each instance warns once");
    }

    [Fact]
    public async Task DeclaredObject_StaysInMemoryOnly()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        (await Load<object>(a, "k", () => 42)).Should().Be(42);

        (await Load<object>(b, "k", () => 42)).Should().BeOfType<int>("B ran its method instead of reading a JsonElement");
        _shared.Get("methodcache:e:k").Should().BeNull();
        Warnings.Should().Be(2, "each instance warns once");
    }

    [Fact]
    public async Task SetAsync_OfAValueTheSerializerCantCarry_RemovesTheValueItReplaced()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();
        (await Load<object>(b, "k", () => "old")).Should().Be("old");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        await a.SetAsync<object>("k", 42, OneMinute);

        var (c, _) = CreateNode();
        (await Load<object>(c, "k", () => "loaded")).Should().Be("loaded", "the replaced value must not stay shared");
    }

    // ---------- Collections declared as interfaces ----------

    [Fact]
    public async Task ReadOnlyListBackedByAnArray_IsShared()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        await Load<IReadOnlyList<int>>(a, "k", () => new[] { 1, 2, 3 });

        var fromL2 = await b.GetOrCreateFastAsync<IReadOnlyList<int>>(
            "k", "M", () => throw new InvalidOperationException("B must read A's list from L2"), Policy());
        fromL2.Should().Equal(1, 2, 3);
        Warnings.Should().Be(0, "a list comes back as another list with the same elements");
    }

    [Fact]
    public async Task EnumerableFromLinq_IsShared()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        await Load<IEnumerable<int>>(a, "k", () => Enumerable.Range(1, 3).Select(i => i * 2));

        var fromL2 = await b.GetOrCreateFastAsync<IEnumerable<int>>(
            "k", "M", () => throw new InvalidOperationException("B must read A's sequence from L2"), Policy());
        fromL2.Should().Equal(2, 4, 6);
        Warnings.Should().Be(0);
    }

    // ---------- A method returning more than one type ----------

    [Fact]
    public async Task DerivedValue_AfterABaseValueWasShared_StaysInMemoryOnly()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        await Load<Animal>(a, "plain", () => new Animal { Name = "a" });
        _shared.Get("methodcache:e:plain").Should().NotBeNull("a plain Animal comes back as it was");

        (await Load<Animal>(a, "dog", () => new Dog { Name = "d", Breed = "b" })).Should().BeOfType<Dog>();
        _shared.Get("methodcache:e:dog").Should().BeNull("a Dog would come back as a plain Animal");
        Warnings.Should().Be(1);

        (await Load<Animal>(b, "dog", () => new Dog { Name = "d", Breed = "b" })).Should().BeOfType<Dog>("B ran its method");
    }

    [Fact]
    public async Task DerivedValue_ForAKeyHoldingABaseValue_RemovesTheBaseValue()
    {
        var (_, layer) = CreateNode();
        await layer.WriteAsync<Animal>("k", new Animal { Name = "old" }, null, layer.Now, OneMinute, methodName: "M");
        _shared.Get("methodcache:e:k").Should().NotBeNull();
        _time.Advance(TimeSpan.FromMilliseconds(1));

        // A refresh, say, that now returns a Dog: other nodes must not go on reading the old Animal
        await layer.WriteAsync<Animal>("k", new Dog { Name = "new", Breed = "b" }, null, layer.Now, OneMinute, methodName: "M");

        _shared.Get("methodcache:e:k").Should().BeNull();
    }

    [Fact]
    public async Task DerivedValue_RejectedBefore_ForAKeyHoldingABaseValue_RemovesTheBaseValue()
    {
        var (_, layer) = CreateNode();
        await layer.WriteAsync<Animal>("first", new Dog { Name = "d", Breed = "b" }, null, layer.Now, OneMinute, methodName: "M");
        await layer.WriteAsync<Animal>("k", new Animal { Name = "old" }, null, layer.Now, OneMinute, methodName: "M");
        _shared.Get("methodcache:e:k").Should().NotBeNull();
        _time.Advance(TimeSpan.FromMilliseconds(1));

        // Dogs were already rejected, so this one isn't serialized; the Animal still must not stay behind
        await layer.WriteAsync<Animal>("k", new Dog { Name = "new", Breed = "b" }, null, layer.Now, OneMinute, methodName: "M");

        _shared.Get("methodcache:e:k").Should().BeNull();
    }

    [Fact]
    public async Task ValueThatCantBeShared_ForAKeyHoldingANull_RemovesTheNull()
    {
        var (_, layer) = CreateNode();
        await layer.WriteAsync<Entity?>("k", null, null, layer.Now, OneMinute, methodName: "M");
        _shared.Get("methodcache:e:k").Should().NotBeNull("a null is shared");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        // A refresh that now finds the entity: other nodes must run the method rather than go on reading null
        await layer.WriteAsync<Entity?>("k", Entity.Create(42), null, layer.Now, OneMinute, methodName: "M");

        _shared.Get("methodcache:e:k").Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_OfADerivedValue_AfterABaseValue_StaysInMemoryOnly()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        await a.SetAsync<Animal>("plain", new Animal { Name = "a" }, OneMinute);
        await a.SetAsync<Animal>("dog", new Dog { Name = "d", Breed = "b" }, OneMinute);

        _shared.Get("methodcache:e:plain").Should().NotBeNull();
        _shared.Get("methodcache:e:dog").Should().BeNull();
        _logger.Messages.Should().ContainSingle(m => m.Message.Contains("SetAsync", StringComparison.Ordinal) && m.Message.Contains("Dog", StringComparison.Ordinal));
        (await Load<Animal>(b, "dog", () => new Dog { Name = "d", Breed = "b" })).Should().BeOfType<Dog>();
    }

    public class Cyclic
    {
        public Cyclic? Next;
    }

    [Fact]
    public async Task ValueTheSerializerThrowsOn_StaysInMemoryOnly_WithOneWarning()
    {
        var serializer = new CountingSerializer(new JsonCacheSerializer());
        var (node, _) = CreateNode(serializer);

        for (var i = 0; i < 3; i++)
        {
            var value = new Cyclic();
            value.Next = value;
            (await Load(node, "k" + i, () => value)).Next.Should().BeSameAs(value);
        }

        serializer.Writes.Should().Be(1, "once the type failed, the method's values of it aren't serialized again");
        _logger.Messages.Count(m => m.Level == LogLevel.Warning).Should().Be(1);
        _shared.Get("methodcache:e:k0").Should().BeNull();
    }

    // ---------- Which values are checked ----------

    private sealed class CountingSerializer(ISerializer inner) : ISerializer
    {
        public int Reads;
        public int Writes;

        public string ContentType => inner.ContentType;

        public void Serialize<T>(T value, IBufferWriter<byte> destination)
        {
            Interlocked.Increment(ref Writes);
            inner.Serialize(value, destination);
        }

        public T? Deserialize<T>(ReadOnlySequence<byte> source)
        {
            Interlocked.Increment(ref Reads);
            return inner.Deserialize<T>(source);
        }
    }

    [Fact]
    public async Task RoundTripCheck_RunsForEveryValue_UntilOneFails()
    {
        var serializer = new CountingSerializer(new JsonCacheSerializer());
        var (node, _) = CreateNode(serializer);

        for (var i = 0; i < 3; i++)
        {
            await Load<Animal>(node, "animal" + i, () => new Animal { Name = "a" });
            await Load<Animal>(node, "dog" + i, () => new Dog { Name = "d" });
        }

        serializer.Reads.Should().Be(4, "every Animal is read back; the Dogs only until the first one failed");
        Warnings.Should().Be(1, "the Dogs are kept in memory with one warning");
    }

    [Fact]
    public async Task EntityWithPrivateSetter_FirstWithItsDefaultId_DoesNotLetLaterIdsThrough()
    {
        var (a, _) = CreateNode();
        var (b, _) = CreateNode();

        // An Id of 0 reads back unchanged, so this one is shared; that says nothing about the next value
        await Load(a, "zero", () => Entity.Create(0));
        (await Load(a, "k", () => Entity.Create(42))).Id.Should().Be(42);

        (await Load(b, "k", () => Entity.Create(42))).Id.Should().Be(42, "B runs its method rather than read an Entity without its id");
        _shared.Get("methodcache:e:k").Should().BeNull();
    }
}
