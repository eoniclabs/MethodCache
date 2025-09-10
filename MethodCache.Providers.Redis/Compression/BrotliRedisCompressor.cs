using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Compression
{
    public class BrotliRedisCompressor : IRedisCompressor
    {
        private readonly int _compressionThreshold;
        private readonly CompressionLevel _compressionLevel;

        public string CompressionType => "brotli";

        public BrotliRedisCompressor(int compressionThreshold = 1024, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            _compressionThreshold = compressionThreshold;
            _compressionLevel = compressionLevel;
        }

        public bool ShouldCompress(byte[]? data)
        {
            return data != null && data.Length > _compressionThreshold;
        }

        public byte[]? Compress(byte[]? data)
        {
            if (data == null || data.Length == 0)
                return data;

            if (!ShouldCompress(data))
                return data;

            using var output = new MemoryStream();
            using (var brotliStream = new BrotliStream(output, _compressionLevel))
            {
                brotliStream.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        public byte[]? Decompress(byte[]? compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return compressedData;

            try
            {
                using var input = new MemoryStream(compressedData);
                using var brotliStream = new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                
                brotliStream.CopyTo(output);
                return output.ToArray();
            }
            catch (Exception)
            {
                // Data is not Brotli compressed or invalid, return as-is
                return compressedData;
            }
        }

        public Task<byte[]?> CompressAsync(byte[]? data)
        {
            return Task.FromResult(Compress(data));
        }

        public Task<byte[]?> DecompressAsync(byte[]? compressedData)
        {
            return Task.FromResult(Decompress(compressedData));
        }
    }
}