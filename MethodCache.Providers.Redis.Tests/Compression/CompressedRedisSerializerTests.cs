using FluentAssertions;
using MethodCache.Providers.Redis.Compression;
using MethodCache.Providers.Redis.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Text;
using Xunit;

namespace MethodCache.Providers.Redis.Tests.Compression;

public class CompressedRedisSerializerTests
{
    private readonly IRedisSerializer _mockInnerSerializer;
    private readonly IRedisCompressor _mockCompressor;
    private readonly ILogger<CompressedRedisSerializer> _mockLogger;
    private readonly CompressedRedisSerializer _serializer;

    public CompressedRedisSerializerTests()
    {
        _mockInnerSerializer = Substitute.For<IRedisSerializer>();
        _mockCompressor = Substitute.For<IRedisCompressor>();
        _mockLogger = Substitute.For<ILogger<CompressedRedisSerializer>>();
        
        _serializer = new CompressedRedisSerializer(
            _mockInnerSerializer,
            _mockCompressor,
            _mockLogger);
    }

    [Fact]
    public void Serialize_WhenCompressionNotNeeded_ShouldReturnSerializedDataDirectly()
    {
        // Arrange
        var testObject = "test data";
        var serializedData = Encoding.UTF8.GetBytes("serialized");
        
        _mockInnerSerializer.Serialize(testObject).Returns(serializedData);
        _mockCompressor.ShouldCompress(serializedData).Returns(false);

        // Act
        var result = _serializer.Serialize(testObject);

        // Assert
        result.Should().BeEquivalentTo(serializedData);
        _mockInnerSerializer.Received(1).Serialize(testObject);
        _mockCompressor.Received(1).ShouldCompress(serializedData);
        _mockCompressor.DidNotReceive().Compress(Arg.Any<byte[]>());
    }

    [Fact]
    public void Serialize_WhenCompressionNeeded_ShouldCompressData()
    {
        // Arrange
        var testObject = "large test data";
        var serializedData = Encoding.UTF8.GetBytes("large serialized data");
        var compressedData = Encoding.UTF8.GetBytes("compressed");
        
        _mockInnerSerializer.Serialize(testObject).Returns(serializedData);
        _mockCompressor.ShouldCompress(serializedData).Returns(true);
        _mockCompressor.Compress(serializedData).Returns(compressedData);
        _mockCompressor.CompressionType.Returns("gzip");

        // Act
        var result = _serializer.Serialize(testObject);

        // Assert
        result.Should().BeEquivalentTo(compressedData);
        _mockInnerSerializer.Received(1).Serialize(testObject);
        _mockCompressor.Received(1).ShouldCompress(serializedData);
        _mockCompressor.Received(1).Compress(serializedData);
    }

    [Fact]
    public void Serialize_WhenCompressionFails_ShouldReturnOriginalData()
    {
        // Arrange
        var testObject = "test data";
        var serializedData = Encoding.UTF8.GetBytes("serialized");
        
        _mockInnerSerializer.Serialize(testObject).Returns(serializedData);
        _mockCompressor.ShouldCompress(serializedData).Returns(true);
        _mockCompressor.When(x => x.Compress(serializedData)).Do(x => { throw new Exception("Compression failed"); });

        // Act
        var result = _serializer.Serialize(testObject);

        // Assert
        result.Should().BeEquivalentTo(serializedData);
        _mockInnerSerializer.Received(1).Serialize(testObject);
    }

    [Fact]
    public void Deserialize_ShouldDecompressAndDeserialize()
    {
        // Arrange
        var compressedData = Encoding.UTF8.GetBytes("compressed");
        var decompressedData = Encoding.UTF8.GetBytes("decompressed");
        var deserializedObject = "result";
        
        _mockCompressor.Decompress(compressedData).Returns(decompressedData);
        _mockInnerSerializer.Deserialize<string>(decompressedData).Returns(deserializedObject);

        // Act
        var result = _serializer.Deserialize<string>(compressedData);

        // Assert
        result.Should().Be(deserializedObject);
        _mockCompressor.Received(1).Decompress(compressedData);
        _mockInnerSerializer.Received(1).Deserialize<string>(decompressedData);
    }

    [Fact]
    public void Deserialize_WhenDecompressionFails_ShouldTryDirectDeserialization()
    {
        // Arrange
        var data = Encoding.UTF8.GetBytes("data");
        var deserializedObject = "result";
        
        _mockCompressor.When(x => x.Decompress(data)).Do(x => { throw new Exception("Decompression failed"); });
        _mockInnerSerializer.Deserialize<string>(data).Returns(deserializedObject);

        // Act
        var result = _serializer.Deserialize<string>(data);

        // Assert
        result.Should().Be(deserializedObject);
        _mockCompressor.Received(1).Decompress(data);
        _mockInnerSerializer.Received(1).Deserialize<string>(data);
    }

    [Fact]
    public async Task SerializeAsync_WhenCompressionNeeded_ShouldCompressDataAsync()
    {
        // Arrange
        var testObject = "large test data";
        var serializedData = Encoding.UTF8.GetBytes("large serialized data");
        var compressedData = Encoding.UTF8.GetBytes("compressed");
        
        _mockInnerSerializer.SerializeAsync(testObject).Returns(serializedData);
        _mockCompressor.ShouldCompress(serializedData).Returns(true);
        _mockCompressor.CompressAsync(serializedData).Returns(compressedData);
        _mockCompressor.CompressionType.Returns("gzip");

        // Act
        var result = await _serializer.SerializeAsync(testObject);

        // Assert
        result.Should().BeEquivalentTo(compressedData);
        _mockInnerSerializer.Received(1).SerializeAsync(testObject);
        _mockCompressor.Received(1).ShouldCompress(serializedData);
        _mockCompressor.Received(1).CompressAsync(serializedData);
    }

    [Fact]
    public async Task DeserializeAsync_ShouldDecompressAndDeserializeAsync()
    {
        // Arrange
        var compressedData = Encoding.UTF8.GetBytes("compressed");
        var decompressedData = Encoding.UTF8.GetBytes("decompressed");
        var deserializedObject = "result";
        
        _mockCompressor.DecompressAsync(compressedData).Returns(decompressedData);
        _mockInnerSerializer.DeserializeAsync<string>(decompressedData).Returns(deserializedObject);

        // Act
        var result = await _serializer.DeserializeAsync<string>(compressedData);

        // Assert
        result.Should().Be(deserializedObject);
        _mockCompressor.Received(1).DecompressAsync(compressedData);
        _mockInnerSerializer.Received(1).DeserializeAsync<string>(decompressedData);
    }
}