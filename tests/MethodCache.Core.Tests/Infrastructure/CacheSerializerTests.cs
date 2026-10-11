using System.Buffers;
using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Serialization.MessagePack;
using Xunit;

namespace MethodCache.Core.Tests.Infrastructure;

public class CacheSerializerTests
{
    public record Order(int Id, string Customer, decimal Total, DateTime PlacedAt, List<string> Lines);

    public static TheoryData<ISerializer> Serializers => new()
    {
        new JsonCacheSerializer(),
        new MessagePackCacheSerializer(),
    };

    [Theory]
    [MemberData(nameof(Serializers))]
    public void RoundTrip_PreservesPrimitives(ISerializer serializer)
    {
        RoundTrip(serializer, 42).Should().Be(42);
        RoundTrip(serializer, "text").Should().Be("text");
        RoundTrip(serializer, true).Should().BeTrue();
        RoundTrip(serializer, 12.5m).Should().Be(12.5m);
        RoundTrip<string?>(serializer, null).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public void RoundTrip_PreservesRecordsAndCollections(ISerializer serializer)
    {
        var order = new Order(7, "Ada", 99.95m, new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), new List<string> { "a", "b" });

        var copy = RoundTrip(serializer, order);

        copy.Should().BeEquivalentTo(order);
        RoundTrip(serializer, new List<int>()).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public void RoundTrip_KeepsDateTimeKind(ISerializer serializer)
    {
        // A value read from the distributed cache must equal the one the method returned, kind included
        foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified })
        {
            var value = new DateTime(2026, 10, 10, 12, 0, 0, kind);

            var copy = RoundTrip(serializer, value);

            copy.Should().Be(value);
            copy.Kind.Should().Be(kind);
        }
    }

    [Theory]
    [MemberData(nameof(Serializers))]
    public void Deserialize_EmptyInput_ReturnsDefault(ISerializer serializer)
    {
        serializer.Deserialize<string>(ReadOnlySequence<byte>.Empty).Should().BeNull();
    }

    [Fact]
    public void Serializers_HaveDistinctContentTypes()
    {
        new JsonCacheSerializer().ContentType.Should().Be("application/json");
        new MessagePackCacheSerializer().ContentType.Should().Be("application/x-msgpack");
    }

    private static T? RoundTrip<T>(ISerializer serializer, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        serializer.Serialize(value, buffer);
        return serializer.Deserialize<T>(new ReadOnlySequence<byte>(buffer.WrittenMemory));
    }
}
