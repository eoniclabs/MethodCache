using FluentAssertions;
using MethodCache.Core.Infrastructure.Serialization;
using Xunit;

namespace MethodCache.Core.Tests.Infrastructure;

public class MessagePackSerializerTests
{
    private readonly MessagePackSerializer _serializer;

    public MessagePackSerializerTests()
    {
        _serializer = new MessagePackSerializer();
    }

    [Fact]
    public void ContentType_ReturnsCorrectMediaType()
    {
        // Act
        var contentType = _serializer.ContentType;

        // Assert
        contentType.Should().Be("application/x-msgpack");
    }

    [Fact]
    public void Serialize_WithStringValue_ReturnsBytes()
    {
        // Arrange
        const string value = "Hello, World!";

        // Act
        var bytes = _serializer.Serialize(value);

        // Assert
        bytes.Should().NotBeNull();
        bytes.Should().NotBeEmpty();
    }

    [Fact]
    public void Deserialize_WithSerializedString_ReturnsOriginalValue()
    {
        // Arrange
        const string originalValue = "Test string for serialization";
        var serializedBytes = _serializer.Serialize(originalValue);

        // Act
        var deserializedValue = _serializer.Deserialize<string>(serializedBytes);

        // Assert
        deserializedValue.Should().Be(originalValue);
    }

    [Fact]
    public void Deserialize_WithSpan_ReturnsOriginalValue()
    {
        // Arrange
        const int originalValue = 12345;
        var serializedBytes = _serializer.Serialize(originalValue);

        // Act
        var deserializedValue = _serializer.Deserialize<int>(serializedBytes.AsSpan());

        // Assert
        deserializedValue.Should().Be(originalValue);
    }

    [Theory]
    [InlineData("Simple string")]
    [InlineData(42)]
    [InlineData(3.14159)]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTrip_WithPrimitiveTypes_PreservesValue<T>(T originalValue)
    {
        // Act
        var serialized = _serializer.Serialize(originalValue);
        var deserialized = _serializer.Deserialize<T>(serialized);

        // Assert
        deserialized.Should().Be(originalValue);
    }

    [Fact]
    public void RoundTrip_WithComplexObject_PreservesAllProperties()
    {
        // Arrange
        var originalObject = new TestObject
        {
            Id = 123,
            Name = "Test Object",
            Tags = new[] { "tag1", "tag2", "tag3" },
            CreatedAt = DateTimeOffset.Now,
            IsActive = true,
            Metadata = new Dictionary<string, string>
            {
                ["key1"] = "value1",
                ["key2"] = "value2"
            }
        };

        // Act
        var serialized = _serializer.Serialize(originalObject);
        var deserialized = _serializer.Deserialize<TestObject>(serialized);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(originalObject.Id);
        deserialized.Name.Should().Be(originalObject.Name);
        deserialized.Tags.Should().BeEquivalentTo(originalObject.Tags);
        deserialized.CreatedAt.Should().BeCloseTo(originalObject.CreatedAt, TimeSpan.FromMilliseconds(1));
        deserialized.IsActive.Should().Be(originalObject.IsActive);
        deserialized.Metadata.Should().BeEquivalentTo(originalObject.Metadata);
    }

    [Fact]
    public void RoundTrip_WithNullValue_ReturnsNull()
    {
        // Arrange
        string? nullValue = null;

        // Act
        var serialized = _serializer.Serialize(nullValue);
        var deserialized = _serializer.Deserialize<string?>(serialized);

        // Assert
        deserialized.Should().BeNull();
    }

    [Fact]
    public void RoundTrip_WithEmptyCollection_PreservesEmptyCollection()
    {
        // Arrange
        var emptyList = new List<string>();

        // Act
        var serialized = _serializer.Serialize(emptyList);
        var deserialized = _serializer.Deserialize<List<string>>(serialized);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_WithEmptyByteArray_ReturnsDefault()
    {
        // Arrange
        var emptyBytes = Array.Empty<byte>();

        // Act
        var result = _serializer.Deserialize<string>(emptyBytes);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WithEmptySpan_ReturnsDefault()
    {
        // Arrange
        var emptySpan = ReadOnlySpan<byte>.Empty;

        // Act
        var result = _serializer.Deserialize<string>(emptySpan);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Serialize_WithInvalidType_ThrowsInvalidOperationException()
    {
        // Arrange
        var invalidObject = new NonSerializableObject();

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => _serializer.Serialize(invalidObject));
        exception.Message.Should().Contain("Failed to serialize object of type NonSerializableObject");
    }

    [Fact]
    public void Deserialize_WithInvalidData_ThrowsInvalidOperationException()
    {
        // Arrange
        var invalidBytes = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => _serializer.Deserialize<string>(invalidBytes));
        exception.Message.Should().Contain("Failed to deserialize data to type String");
    }

    [Fact]
    public void Serialization_IsCompressed()
    {
        // Arrange
        var largeString = new string('A', 10000); // 10KB of 'A' characters

        // Act
        var serializedBytes = _serializer.Serialize(largeString);

        // Assert
        // With compression, the serialized data should be much smaller than the original
        serializedBytes.Length.Should().BeLessThan(largeString.Length);
    }

    // Test object for complex serialization scenarios
    public class TestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string[] Tags { get; set; } = Array.Empty<string>();
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsActive { get; set; }
        public Dictionary<string, string> Metadata { get; set; } = new();
    }

    // Object that cannot be serialized with MessagePack (contains non-serializable types)
    public class NonSerializableObject
    {
        public IntPtr Pointer { get; set; } = new IntPtr(12345);
        public Delegate Action { get; set; } = new Action(() => { });
    }
}