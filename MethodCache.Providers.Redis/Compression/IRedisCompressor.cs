using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Compression
{
    public interface IRedisCompressor
    {
        byte[]? Compress(byte[]? data);
        byte[]? Decompress(byte[]? compressedData);
        Task<byte[]?> CompressAsync(byte[]? data);
        Task<byte[]?> DecompressAsync(byte[]? compressedData);
        string CompressionType { get; }
        bool ShouldCompress(byte[]? data);
    }
}