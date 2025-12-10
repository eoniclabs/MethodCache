using MethodCache.Providers.Redis.Configuration;
using Xunit;

namespace MethodCache.Providers.Redis.Tests.Configuration
{
    public class RedisSerializerFactoryTests
    {
        [Fact]
        public void Create_WithBinarySerializerType_ReturnsBinarySerializer()
        {
            var factory = new RedisSerializerFactory();

            var serializer = factory.Create(RedisSerializerType.Binary);

            Assert.IsType<BinaryRedisSerializer>(serializer);

            var payload = new[] { 1, 2, 3 };
            var bytes = serializer.Serialize(payload);
            var roundTrip = serializer.Deserialize<int[]>(bytes);

            Assert.Equal(payload, roundTrip);
        }
    }
}
