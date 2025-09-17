using System;
using System.Buffers;
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

        public async Task<byte[]?> CompressAsync(byte[]? data)
        {
            if (data == null || data.Length == 0)
                return data;

            if (!ShouldCompress(data))
                return data;

            // Use ArrayPool for better memory efficiency
            var buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                using var output = new MemoryStream();
                using (var brotliStream = new BrotliStream(output, _compressionLevel, leaveOpen: true))
                {
                    // Use async write for true async operation
                    await brotliStream.WriteAsync(data, 0, data.Length);
                    await brotliStream.FlushAsync();
                }
                return output.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        public async Task<byte[]?> DecompressAsync(byte[]? compressedData)
        {
            if (compressedData == null || compressedData.Length == 0)
                return compressedData;

            try
            {
                using var input = new MemoryStream(compressedData);
                using var brotliStream = new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                
                // Use async copy for true async operation
                await brotliStream.CopyToAsync(output, bufferSize: 81920); // 80KB buffer
                return output.ToArray();
            }
            catch (Exception)
            {
                // Data is not Brotli compressed or invalid, return as-is
                return compressedData;
            }
        }
    }
}