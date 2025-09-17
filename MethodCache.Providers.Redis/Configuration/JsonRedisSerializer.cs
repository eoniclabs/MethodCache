using System.Text.Json;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public class JsonRedisSerializer : IRedisSerializer
    {
        private readonly JsonSerializerOptions _options;

        public JsonRedisSerializer(JsonSerializerOptions? options = null)
        {
            _options = options ?? new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };
        }

        public byte[] Serialize<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            // Use more efficient UTF8 byte serialization
            return JsonSerializer.SerializeToUtf8Bytes(value, _options);
        }

        public T Deserialize<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                return default(T)!;

            // Use more efficient UTF8 byte deserialization
            return JsonSerializer.Deserialize<T>(data, _options)!;
        }

        public async Task<byte[]> SerializeAsync<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            using var stream = new MemoryStream();
            await JsonSerializer.SerializeAsync(stream, value, _options);
            return stream.ToArray();
        }

        public async Task<T> DeserializeAsync<T>(byte[] data)
        {
            if (data == null || data.Length == 0)
                return default(T)!;

            using var stream = new MemoryStream(data, writable: false);
            return (await JsonSerializer.DeserializeAsync<T>(stream, _options))!;
        }
    }
}