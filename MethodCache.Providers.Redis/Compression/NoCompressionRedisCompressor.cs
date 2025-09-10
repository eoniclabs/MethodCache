using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Compression
{
    public class NoCompressionRedisCompressor : IRedisCompressor
    {
        public string CompressionType => "none";

        public bool ShouldCompress(byte[] data) => false;

        public byte[] Compress(byte[] data) => data;

        public byte[] Decompress(byte[] compressedData) => compressedData;

        public Task<byte[]> CompressAsync(byte[] data) => Task.FromResult(data);

        public Task<byte[]> DecompressAsync(byte[] compressedData) => Task.FromResult(compressedData);
    }
}