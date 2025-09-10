using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public class BinaryRedisSerializer : IRedisSerializer
    {
        public byte[] Serialize<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            // Use System.Text.Json as the primary serialization method in .NET 9.0
            // BinaryFormatter is obsolete and has security concerns
            var json = System.Text.Json.JsonSerializer.Serialize(value);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        public T Deserialize<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                return default(T)!;

            // Use System.Text.Json as the primary deserialization method
            var json = System.Text.Encoding.UTF8.GetString(data);
            return System.Text.Json.JsonSerializer.Deserialize<T>(json)!;
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