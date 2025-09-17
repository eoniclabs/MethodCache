using MessagePack;
using MessagePack.Resolvers;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public class MessagePackRedisSerializer : IRedisSerializer
    {
        private static readonly MessagePackSerializerOptions _secureOptions = 
            MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

        public byte[] Serialize<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            return MessagePackSerializer.Serialize(value, _secureOptions);
        }

        public T Deserialize<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                return default(T)!;

            return MessagePackSerializer.Deserialize<T>(data, _secureOptions);
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