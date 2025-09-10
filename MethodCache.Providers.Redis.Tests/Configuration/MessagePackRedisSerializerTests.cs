using Xunit;
using MethodCache.Providers.Redis.Configuration;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Tests.Configuration
{
    public class MessagePackRedisSerializerTests
    {
        private readonly MessagePackRedisSerializer _serializer;

        public MessagePackRedisSerializerTests()
        {
            _serializer = new MessagePackRedisSerializer();
        }

        [Fact]
        public void Serialize_WithValidObject_ReturnsBytes()
        {
            // Arrange
            var testObject = new TestData { Id = 123, Name = "Test" };

            // Act
            var result = _serializer.Serialize(testObject);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Length > 0);
        }

        [Fact]
        public void Serialize_WithNull_ReturnsEmptyArray()
        {
            // Act
            var result = _serializer.Serialize<object?>(null);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void Deserialize_WithValidBytes_ReturnsObject()
        {
            // Arrange
            var testObject = new TestData { Id = 456, Name = "Another Test" };
            var bytes = _serializer.Serialize(testObject);

            // Act
            var result = _serializer.Deserialize<TestData>(bytes);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(testObject.Id, result.Id);
            Assert.Equal(testObject.Name, result.Name);
        }

        [Fact]
        public void Deserialize_WithEmptyBytes_ReturnsDefault()
        {
            // Act
            var result = _serializer.Deserialize<TestData>(Array.Empty<byte>());

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task SerializeAsync_WithValidObject_ReturnsBytes()
        {
            // Arrange
            var testObject = new TestData { Id = 789, Name = "Async Test" };

            // Act
            var result = await _serializer.SerializeAsync(testObject);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Length > 0);
        }

        [Fact]
        public async Task DeserializeAsync_WithValidBytes_ReturnsObject()
        {
            // Arrange
            var testObject = new TestData { Id = 101112, Name = "Async Deserialize Test" };
            var bytes = await _serializer.SerializeAsync(testObject);

            // Act
            var result = await _serializer.DeserializeAsync<TestData>(bytes);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(testObject.Id, result.Id);
            Assert.Equal(testObject.Name, result.Name);
        }

        private class TestData
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
        }
    }
}