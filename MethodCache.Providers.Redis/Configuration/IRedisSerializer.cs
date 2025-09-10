using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public interface IRedisSerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(byte[] data);
        Task<byte[]> SerializeAsync<T>(T value);
        Task<T> DeserializeAsync<T>(byte[] data);
    }
}