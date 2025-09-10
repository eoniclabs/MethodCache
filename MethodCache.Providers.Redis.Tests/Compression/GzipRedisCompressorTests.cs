using FluentAssertions;
using MethodCache.Providers.Redis.Compression;
using System.Text;
using Xunit;

namespace MethodCache.Providers.Redis.Tests.Compression;

public class GzipRedisCompressorTests
{
    [Fact]
    public void Compress_SmallData_ShouldNotCompress()
    {
        // Arrange
        var compressor = new GzipRedisCompressor(compressionThreshold: 100);
        var smallData = Encoding.UTF8.GetBytes("small data");

        // Act
        var result = compressor.Compress(smallData);

        // Assert
        result.Should().BeEquivalentTo(smallData);
        compressor.ShouldCompress(smallData).Should().BeFalse();
    }

    [Fact]
    public void Compress_LargeData_ShouldCompress()
    {
        // Arrange
        var compressor = new GzipRedisCompressor(compressionThreshold: 10);
        var largeData = Encoding.UTF8.GetBytes(new string('A', 1000));

        // Act
        var result = compressor.Compress(largeData);

        // Assert
        result.Should().NotBeEquivalentTo(largeData);
        result.Length.Should().BeLessThan(largeData.Length);
        compressor.ShouldCompress(largeData).Should().BeTrue();
    }

    [Fact]
    public void CompressDecompress_Roundtrip_ShouldReturnOriginalData()
    {
        // Arrange
        var compressor = new GzipRedisCompressor(compressionThreshold: 10);
        var originalData = Encoding.UTF8.GetBytes("This is some test data that should be compressed and then decompressed back to the original form.");

        // Act
        var compressed = compressor.Compress(originalData);
        var decompressed = compressor.Decompress(compressed);

        // Assert
        decompressed.Should().BeEquivalentTo(originalData);
        Encoding.UTF8.GetString(decompressed).Should().Be(Encoding.UTF8.GetString(originalData));
    }

    [Fact]
    public void Decompress_UncompressedData_ShouldReturnAsIs()
    {
        // Arrange
        var compressor = new GzipRedisCompressor();
        var uncompressedData = Encoding.UTF8.GetBytes("This is not compressed");

        // Act
        var result = compressor.Decompress(uncompressedData);

        // Assert
        result.Should().BeEquivalentTo(uncompressedData);
    }

    [Fact]
    public void Compress_NullData_ShouldReturnNull()
    {
        // Arrange
        var compressor = new GzipRedisCompressor();

        // Act
        var result = compressor.Compress(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Compress_EmptyData_ShouldReturnEmpty()
    {
        // Arrange
        var compressor = new GzipRedisCompressor();
        var emptyData = new byte[0];

        // Act
        var result = compressor.Compress(emptyData);

        // Assert
        result.Should().BeEquivalentTo(emptyData);
    }

    [Fact]
    public async Task CompressAsync_ShouldMatchSyncVersion()
    {
        // Arrange
        var compressor = new GzipRedisCompressor(compressionThreshold: 10);
        var data = Encoding.UTF8.GetBytes("This is test data for async compression");

        // Act
        var syncResult = compressor.Compress(data);
        var asyncResult = await compressor.CompressAsync(data);

        // Assert
        asyncResult.Should().BeEquivalentTo(syncResult);
    }

    [Fact]
    public async Task DecompressAsync_ShouldMatchSyncVersion()
    {
        // Arrange
        var compressor = new GzipRedisCompressor(compressionThreshold: 10);
        var originalData = Encoding.UTF8.GetBytes("This is test data for async decompression");
        var compressedData = compressor.Compress(originalData);

        // Act
        var syncResult = compressor.Decompress(compressedData);
        var asyncResult = await compressor.DecompressAsync(compressedData);

        // Assert
        asyncResult.Should().BeEquivalentTo(syncResult);
        asyncResult.Should().BeEquivalentTo(originalData);
    }
}