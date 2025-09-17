using System;

namespace MethodCache.Providers.Redis.Configuration
{
    public interface IRedisSerializerFactory
    {
        IRedisSerializer Create(RedisSerializerType serializerType);
    }

    public class RedisSerializerFactory : IRedisSerializerFactory
    {
        public IRedisSerializer Create(RedisSerializerType serializerType)
        {
            return serializerType switch
            {
                RedisSerializerType.MessagePack => new MessagePackRedisSerializer(),
                RedisSerializerType.Json => new JsonRedisSerializer(),
                RedisSerializerType.Binary => new JsonRedisSerializer(),
                _ => throw new ArgumentOutOfRangeException(nameof(serializerType), serializerType, null)
            };
        }
    }
}