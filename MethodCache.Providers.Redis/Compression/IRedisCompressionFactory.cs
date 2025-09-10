using MethodCache.Providers.Redis.Configuration;

namespace MethodCache.Providers.Redis.Compression
{
    public interface IRedisCompressionFactory
    {
        IRedisCompressor Create(RedisCompressionType compressionType, int compressionThreshold = 1024);
    }

    public class RedisCompressionFactory : IRedisCompressionFactory
    {
        public IRedisCompressor Create(RedisCompressionType compressionType, int compressionThreshold = 1024)
        {
            return compressionType switch
            {
                RedisCompressionType.Gzip => new GzipRedisCompressor(compressionThreshold),
                RedisCompressionType.Brotli => new BrotliRedisCompressor(compressionThreshold),
                RedisCompressionType.None => new NoCompressionRedisCompressor(),
                _ => new NoCompressionRedisCompressor()
            };
        }
    }
}