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

    // ---------- The check runs once per method ----------

    private sealed class CountingSerializer(ISerializer inner) : ISerializer
    {
        public int Reads;

        public string ContentType => inner.ContentType;

        public void Serialize<T>(T value, IBufferWriter<byte> destination) => inner.Serialize(value, destination);

        public T? Deserialize<T>(ReadOnlySequence<byte> source)
        {
            Interlocked.Increment(ref Reads);
            return inner.Deserialize<T>(source);
        }
    }

    [Fact]
    public async Task RoundTripCheck_RunsOncePerMethod()
    {
        var serializer = new CountingSerializer(new JsonCacheSerializer());
        var (node, _) = CreateNode(serializer);

        for (var i = 0; i < 5; i++)
        {
            await Load(node, "a" + i, () => (Total: i, Name: "x"), method: "A");
        }

        serializer.Reads.Should().Be(1, "the first value of a method is read back once; later writes trust that");

        await Load(node, "b", () => (Total: 1, Name: "x"), method: "B");
        serializer.Reads.Should().Be(2, "each method is checked on its own");
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _messages = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Messages
        {
            get { lock (_messages) return _messages.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add((level, formatter(state, error)));
        }
    }
}
