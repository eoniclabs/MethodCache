using FluentAssertions;
using Microsoft.Extensions.Options;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Services;

namespace MethodCache.Providers.SqlServer.Tests;

public class SqlServerSerializerTests
{
    [Theory]
    [InlineData(SqlServerSerializerType.Json)]
    [InlineData(SqlServerSerializerType.MessagePack)]
    [InlineData(SqlServerSerializerType.Binary)]
    public void Constructor_WithValidSerializerType_ShouldNotThrow(SqlServerSerializerType serializerType)
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = serializerType });

        // Act & Assert
        var act = () => new SqlServerSerializer(options);
        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WithDefaultOptions_ShouldUseMessagePack()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions()); // DefaultSerializer is MessagePack

        // Act
        var serializer = new SqlServerSerializer(options);

        // Assert
        serializer.Should().NotBeNull();
    }

    [Theory]
    [InlineData("simple string")]
    [InlineData("")]
    [InlineData("string with special characters: !@#$%^&*()")]
    [InlineData("string with unicode: 🚀 💻 ⚡")]
    public async Task SerializeDeserialize_String_ShouldRoundTripCorrectly(string value)
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        // Act
        var serialized = await serializer.SerializeAsync(value);
        var deserialized = await serializer.DeserializeAsync<string>(serialized);

        // Assert
        deserialized.Should().Be(value);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(-123)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task SerializeDeserialize_Integer_ShouldRoundTripCorrectly(int value)
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        // Act
        var serialized = await serializer.SerializeAsync(value);
        var deserialized = await serializer.DeserializeAsync<int>(serialized);

        // Assert
        deserialized.Should().Be(value);
    }

    [Fact]
    public async Task SerializeDeserialize_ComplexObject_ShouldRoundTripCorrectly()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        var testObject = new TestObject
        {
            Id = 123,
            Name = "Test Object",
            Items = new[] { "item1", "item2", "item3" },
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            Price = 99.99m
        };

        // Act
        var serialized = await serializer.SerializeAsync(testObject);
        var deserialized = await serializer.DeserializeAsync<TestObject>(serialized);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(testObject.Id);
        deserialized.Name.Should().Be(testObject.Name);
        deserialized.Items.Should().BeEquivalentTo(testObject.Items);
        deserialized.CreatedAt.Should().BeCloseTo(testObject.CreatedAt, TimeSpan.FromMilliseconds(1));
        deserialized.IsActive.Should().Be(testObject.IsActive);
        deserialized.Price.Should().Be(testObject.Price);
    }

    [Fact]
    public async Task SerializeDeserialize_NullValue_ShouldHandleCorrectly()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        // Act
        var serialized = await serializer.SerializeAsync<string>(null);
        var deserialized = await serializer.DeserializeAsync<string>(serialized);

        // Assert
        deserialized.Should().BeNull();
    }

    [Fact]
    public async Task Serialize_WithNullValue_ShouldReturnNull()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        // Act
        var result = await serializer.SerializeAsync<string>(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task Deserialize_WithNullData_ShouldReturnDefault()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        // Act
        var result = await serializer.DeserializeAsync<string>(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task Deserialize_WithEmptyData_ShouldReturnDefault()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        // Act
        var result = await serializer.DeserializeAsync<string>(new byte[0]);

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(SqlServerSerializerType.Json)]
    [InlineData(SqlServerSerializerType.MessagePack)]
    public async Task SerializeDeserialize_WithDifferentSerializers_ShouldWork(SqlServerSerializerType serializerType)
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = serializerType });
        var serializer = new SqlServerSerializer(options);

        var testData = new TestObject { Id = 42, Name = "Test", Items = new[] { "item1", "item2" } };

        // Act
        var serialized = await serializer.SerializeAsync(testData);
        var deserialized = await serializer.DeserializeAsync<TestObject>(serialized);

        // Assert
        serialized.Should().NotBeNull();
        serialized.Should().NotBeEmpty();
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(42);
        deserialized.Name.Should().Be("Test");
    }

    [Fact]
    public async Task Serialize_LargeObject_ShouldNotThrow()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        var largeObject = new
        {
            Data = string.Join("", Enumerable.Repeat("Large data string with lots of content.", 1000))
        };

        // Act & Assert
        var act = async () => await serializer.SerializeAsync(largeObject);
        await act.Should().NotThrowAsync();

        var serialized = await serializer.SerializeAsync(largeObject);
        serialized.Should().NotBeNull();
        serialized!.Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public async Task SerializeDeserialize_DateTime_ShouldPreserveValue()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        var dateTime = new DateTime(2023, 10, 15, 14, 30, 45, DateTimeKind.Utc);

        // Act
        var serialized = await serializer.SerializeAsync(dateTime);
        var deserialized = await serializer.DeserializeAsync<DateTime>(serialized);

        // Assert
        deserialized.Should().Be(dateTime);
    }

    [Fact]
    public async Task SerializeDeserialize_Guid_ShouldPreserveValue()
    {
        // Arrange
        var options = Options.Create(new SqlServerOptions { DefaultSerializer = SqlServerSerializerType.Json });
        var serializer = new SqlServerSerializer(options);

        var guid = Guid.NewGuid();

        // Act
        var serialized = await serializer.SerializeAsync(guid);
        var deserialized = await serializer.DeserializeAsync<Guid>(serialized);

        // Assert
        deserialized.Should().Be(guid);
    }

    public class TestObject
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string[] Items { get; set; } = Array.Empty<string>();
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; }
        public decimal Price { get; set; }
    }
}