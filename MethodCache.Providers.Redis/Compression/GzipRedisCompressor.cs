using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Compression
{
    public class GzipRedisCompressor : IRedisCompressor
    {
        private readonly int _compressionThreshold;
        private readonly CompressionLevel _compressionLevel;

        public string CompressionType => "gzip";

        public GzipRedisCompressor(int compressionThreshold = 1024, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            _compressionThreshold = compressionThreshold;
            _compressionLevel = compressionLevel;
        }

        public bool ShouldCompress(byte[] data)
        {
            return data != null && data.Length > _compressionThreshold;
        }

        public byte[] Compress(byte[] data)
        {
            if (data == null || data.Length == 0)
                return data;

            if (!ShouldCompress(data))
                return data;

            using var output = new MemoryStream();
            using (var gzipStream = new GZipStream(output, _compressionLevel))
            {
                gzipStream.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        public byte[] Decompress(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return compressedData;

            // Check if data is actually compressed (starts with gzip magic number)
            if (compressedData.Length < 2 || compressedData[0] != 0x1F || compressedData[1] != 0x8B)
                return compressedData;

            using var input = new MemoryStream(compressedData);
            using var gzipStream = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            
            gzipStream.CopyTo(output);
            return output.ToArray();
        }

        public Task<byte[]> CompressAsync(byte[] data)
        {
            return Task.FromResult(Compress(data));
        }

        public Task<byte[]> DecompressAsync(byte[] compressedData)
        {
            return Task.FromResult(Decompress(compressedData));
        }
    }
}