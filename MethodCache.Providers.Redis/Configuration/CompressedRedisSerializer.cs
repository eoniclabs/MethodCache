using MethodCache.Providers.Redis.Compression;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public class CompressedRedisSerializer : IRedisSerializer
    {
        private readonly IRedisSerializer _innerSerializer;
        private readonly IRedisCompressor _compressor;
        private readonly ILogger<CompressedRedisSerializer> _logger;

        public CompressedRedisSerializer(
            IRedisSerializer innerSerializer,
            IRedisCompressor compressor,
            ILogger<CompressedRedisSerializer> logger)
        {
            _innerSerializer = innerSerializer;
            _compressor = compressor;
            _logger = logger;
        }

        public byte[] Serialize<T>(T value)
        {
            try
            {
                var serializedData = _innerSerializer.Serialize(value);
                
                if (!_compressor.ShouldCompress(serializedData))
                {
                    _logger.LogTrace("Skipping compression for data of size {Size} bytes (below threshold)", serializedData?.Length ?? 0);
                    return serializedData ?? Array.Empty<byte>();
                }

                var compressedData = _compressor.Compress(serializedData);
                var originalSize = serializedData?.Length ?? 0;
                var compressedSize = compressedData?.Length ?? 0;
                var compressionRatio = originalSize > 0 ? (double)compressedSize / originalSize : 1.0;

                _logger.LogDebug("Compressed data from {OriginalSize} to {CompressedSize} bytes (ratio: {CompressionRatio:P2}) using {CompressionType}",
                    originalSize, compressedSize, compressionRatio, _compressor.CompressionType);

                return compressedData ?? Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compress data, returning uncompressed");
                return _innerSerializer.Serialize(value);
            }
        }

        public T Deserialize<T>(byte[] data)
        {
            try
            {
                var decompressedData = _compressor.Decompress(data);
                return _innerSerializer.Deserialize<T>(decompressedData ?? Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decompress data, attempting direct deserialization");
                return _innerSerializer.Deserialize<T>(data);
            }
        }

        public async Task<byte[]> SerializeAsync<T>(T value)
        {
            try
            {
                var serializedData = await _innerSerializer.SerializeAsync(value);
                
                if (!_compressor.ShouldCompress(serializedData))
                {
                    _logger.LogTrace("Skipping compression for data of size {Size} bytes (below threshold)", serializedData?.Length ?? 0);
                    return serializedData ?? Array.Empty<byte>();
                }

                var compressedData = await _compressor.CompressAsync(serializedData);
                var originalSize = serializedData?.Length ?? 0;
                var compressedSize = compressedData?.Length ?? 0;
                var compressionRatio = originalSize > 0 ? (double)compressedSize / originalSize : 1.0;

                _logger.LogDebug("Compressed data from {OriginalSize} to {CompressedSize} bytes (ratio: {CompressionRatio:P2}) using {CompressionType}",
                    originalSize, compressedSize, compressionRatio, _compressor.CompressionType);

                return compressedData ?? Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compress data, returning uncompressed");
                return await _innerSerializer.SerializeAsync(value);
            }
        }

        public async Task<T> DeserializeAsync<T>(byte[] data)
        {
            try
            {
                var decompressedData = await _compressor.DecompressAsync(data);
                return await _innerSerializer.DeserializeAsync<T>(decompressedData ?? Array.Empty<byte>());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decompress data, attempting direct deserialization");
                return await _innerSerializer.DeserializeAsync<T>(data);
            }
        }
    }
}