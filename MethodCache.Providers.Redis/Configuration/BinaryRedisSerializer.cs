using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    /// <summary>
    /// Binary serializer that leverages MessagePack with strict contracts to provide compact binary payloads.
    /// </summary>
    public sealed class BinaryRedisSerializer : IRedisSerializer
    {
        private readonly MessagePackRedisSerializer _inner = new(useContractless: false);

        public byte[] Serialize<T>(T value) => _inner.Serialize(value);

        public T Deserialize<T>(byte[] data) => _inner.Deserialize<T>(data);

        public Task<byte[]> SerializeAsync<T>(T value) => _inner.SerializeAsync(value);

        public Task<T> DeserializeAsync<T>(byte[] data) => _inner.DeserializeAsync<T>(data);
    }
}
