using MessagePack;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public class MessagePackRedisSerializer : IRedisSerializer
    {
        public byte[] Serialize<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            return MessagePackSerializer.Typeless.Serialize(value);
        }

        public T Deserialize<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                return default(T)!;

            var result = MessagePackSerializer.Typeless.Deserialize(data);
            return result is T typedResult ? typedResult : default(T)!;
        }

        public Task<byte[]> SerializeAsync<T>(T value)
        {
            return Task.FromResult(Serialize(value));
        }

        public Task<T> DeserializeAsync<T>(byte[] data)
        {
            return Task.FromResult(Deserialize<T>(data));
        }
    }
}