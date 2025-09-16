using FluentAssertions;
using MethodCache.Core.Configuration;
using MethodCache.ETags.Implementation;
using MethodCache.ETags.Models;
using MethodCache.HybridCache.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.ETags.Tests.Implementation
{
    public class ETagHybridCacheManagerTests
    {
        private readonly Mock<IHybridCacheManager> _mockHybridCache;
        private readonly Mock<ILogger<ETagHybridCacheManager>> _mockLogger;
        private readonly ETagHybridCacheManager _cacheManager;
        private readonly CacheMethodSettings _defaultSettings;

        public ETagHybridCacheManagerTests()
        {
            _mockHybridCache = new Mock<IHybridCacheManager>();
            _mockLogger = new Mock<ILogger<ETagHybridCacheManager>>();
            _cacheManager = new ETagHybridCacheManager(_mockHybridCache.Object, _mockLogger.Object);
            _defaultSettings = new CacheMethodSettings 
            { 
                Duration = TimeSpan.FromMinutes(30),
                Tags = new[] { "test-tag" }
            };
        }

        [Fact]
        public async Task GetOrCreateWithETagAsync_CacheHit_ShouldReturnCachedValue()
        {
            // Arrange
            var key = "test-key";
            var cachedValue = "cached-value";
            var cachedETag = "\"cached-etag\"";
            var ifNoneMatch = "\"different-etag\"";
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>(key))
                .ReturnsAsync(cachedValue);
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>($"etag:{key}"))
                .ReturnsAsync(cachedETag);

            // Act
            var result = await _cacheManager.GetOrCreateWithETagAsync(
                key,
                () => Task.FromResult(ETagCacheEntry<string>.WithValue("new-value", "\"new-etag\"")),
                ifNoneMatch,
                _defaultSettings);

            // Assert
            result.Should().NotBeNull();
            result.Value.Should().Be(cachedValue);
            result.ETag.Should().Be(cachedETag);
            result.Status.Should().Be(ETagCacheStatus.Hit);
            result.ShouldReturn304.Should().BeFalse();
        }

        [Fact]
        public async Task GetOrCreateWithETagAsync_ETagMatch_ShouldReturn304()
        {
            // Arrange
            var key = "test-key";
            var cachedETag = "\"matching-etag\"";
            var ifNoneMatch = "\"matching-etag\"";
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>($"etag:{key}"))
                .ReturnsAsync(cachedETag);

            // Act
            var result = await _cacheManager.GetOrCreateWithETagAsync(
                key,
                () => Task.FromResult(ETagCacheEntry<string>.WithValue("new-value", "\"new-etag\"")),
                ifNoneMatch,
                _defaultSettings);

            // Assert
            result.Should().NotBeNull();
            result.ETag.Should().Be(cachedETag);
            result.Status.Should().Be(ETagCacheStatus.NotModified);
            result.ShouldReturn304.Should().BeTrue();
        }

        [Fact]
        public async Task GetOrCreateWithETagAsync_CacheMiss_ShouldExecuteFactory()
        {
            // Arrange
            var key = "test-key";
            var factoryValue = "factory-value";
            var factoryETag = "\"factory-etag\"";
            var ifNoneMatch = "\"different-etag\"";
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>(key))
                .ReturnsAsync((string?)null);
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>($"etag:{key}"))
                .ReturnsAsync((string?)null);

            _mockHybridCache
                .Setup(x => x.GetFromL2Async<string>(key))
                .ReturnsAsync((string?)null);

            _mockHybridCache
                .Setup(x => x.GetFromL2Async<string>($"etag:{key}"))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _cacheManager.GetOrCreateWithETagAsync(
                key,
                () => Task.FromResult(ETagCacheEntry<string>.WithValue(factoryValue, factoryETag)),
                ifNoneMatch,
                _defaultSettings);

            // Assert
            result.Should().NotBeNull();
            result.Value.Should().Be(factoryValue);
            result.ETag.Should().Be(factoryETag);
            result.Status.Should().Be(ETagCacheStatus.Miss);
            result.ShouldReturn304.Should().BeFalse();

            // Verify storage calls
            _mockHybridCache.Verify(x => x.SetInL1Async(key, factoryValue, It.IsAny<TimeSpan>(), _defaultSettings.Tags), Times.Once);
            _mockHybridCache.Verify(x => x.SetInL1Async($"etag:{key}", factoryETag, It.IsAny<TimeSpan>()), Times.Once);
            _mockHybridCache.Verify(x => x.SetInL2Async(key, factoryValue, It.IsAny<TimeSpan>()), Times.Once);
            _mockHybridCache.Verify(x => x.SetInL2Async($"etag:{key}", factoryETag, It.IsAny<TimeSpan>()), Times.Once);
        }

        [Fact]
        public async Task GetOrCreateWithETagAsync_L2Hit_ShouldWarmL1Cache()
        {
            // Arrange
            var key = "test-key";
            var l2Value = "l2-value";
            var l2ETag = "\"l2-etag\"";
            var ifNoneMatch = "\"different-etag\"";
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>(key))
                .ReturnsAsync((string?)null);
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>($"etag:{key}"))
                .ReturnsAsync((string?)null);

            _mockHybridCache
                .Setup(x => x.GetFromL2Async<string>(key))
                .ReturnsAsync(l2Value);

            _mockHybridCache
                .Setup(x => x.GetFromL2Async<string>($"etag:{key}"))
                .ReturnsAsync(l2ETag);

            // Act
            var result = await _cacheManager.GetOrCreateWithETagAsync(
                key,
                () => Task.FromResult(ETagCacheEntry<string>.WithValue("factory-value", "\"factory-etag\"")),
                ifNoneMatch,
                _defaultSettings);

            // Assert
            result.Should().NotBeNull();
            result.Value.Should().Be(l2Value);
            result.ETag.Should().Be(l2ETag);
            result.Status.Should().Be(ETagCacheStatus.Hit);
            result.ShouldReturn304.Should().BeFalse();

            // Verify L1 warming
            _mockHybridCache.Verify(x => x.SetInL1Async(key, l2Value, It.IsAny<TimeSpan>()), Times.Once);
            _mockHybridCache.Verify(x => x.SetInL1Async($"etag:{key}", l2ETag, It.IsAny<TimeSpan>()), Times.Once);
        }

        [Fact]
        public async Task GetOrCreateWithETagAsync_WithoutTags_ShouldUseThreeParameterOverload()
        {
            // Arrange
            var key = "test-key";
            var factoryValue = "factory-value";
            var factoryETag = "\"factory-etag\"";
            var settingsWithoutTags = new CacheMethodSettings 
            { 
                Duration = TimeSpan.FromMinutes(30),
                Tags = null
            };
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>(key))
                .ReturnsAsync((string?)null);
            
            _mockHybridCache
                .Setup(x => x.GetFromL2Async<string>(key))
                .ReturnsAsync((string?)null);

            // Act
            var result = await _cacheManager.GetOrCreateWithETagAsync(
                key,
                () => Task.FromResult(ETagCacheEntry<string>.WithValue(factoryValue, factoryETag)),
                null,
                settingsWithoutTags);

            // Assert
            result.Should().NotBeNull();
            result.Value.Should().Be(factoryValue);
            result.ETag.Should().Be(factoryETag);

            // Verify the 3-parameter overload was called (without tags)
            _mockHybridCache.Verify(x => x.SetInL1Async(key, factoryValue, It.IsAny<TimeSpan>()), Times.Once);
            _mockHybridCache.Verify(x => x.SetInL1Async(key, factoryValue, It.IsAny<TimeSpan>(), It.IsAny<IEnumerable<string>>()), Times.Never);
        }

        [Fact]
        public async Task InvalidateETagAsync_ShouldInvalidateBothValueAndETag()
        {
            // Arrange
            var key = "test-key";

            // Act
            await _cacheManager.InvalidateETagAsync(key);

            // Assert
            _mockHybridCache.Verify(x => x.InvalidateBothAsync(key), Times.Once);
            _mockHybridCache.Verify(x => x.InvalidateBothAsync($"etag:{key}"), Times.Once);
        }

        [Fact]
        public async Task InvalidateETagsAsync_ShouldInvalidateMultipleKeys()
        {
            // Arrange
            var keys = new[] { "key1", "key2", "key3" };

            // Act
            await _cacheManager.InvalidateETagsAsync(keys);

            // Assert
            foreach (var key in keys)
            {
                _mockHybridCache.Verify(x => x.InvalidateBothAsync(key), Times.Once);
                _mockHybridCache.Verify(x => x.InvalidateBothAsync($"etag:{key}"), Times.Once);
            }
        }

        [Fact]
        public async Task GetOrCreateWithETagAsync_FactoryThrowsException_ShouldPropagateException()
        {
            // Arrange
            var key = "test-key";
            var expectedException = new InvalidOperationException("Factory failed");
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>(key))
                .ReturnsAsync((string?)null);
            
            _mockHybridCache
                .Setup(x => x.GetFromL2Async<string>(key))
                .ReturnsAsync((string?)null);

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _cacheManager.GetOrCreateWithETagAsync(
                    key,
                    () => throw expectedException,
                    null,
                    _defaultSettings));

            exception.Should().Be(expectedException);
        }

        [Theory]
        [InlineData("*")]
        [InlineData("\"*\"")]
        public async Task GetOrCreateWithETagAsync_IfNoneMatchStar_ShouldReturn304(string ifNoneMatch)
        {
            // Arrange
            var key = "test-key";
            
            // Act
            var result = await _cacheManager.GetOrCreateWithETagAsync(
                key,
                () => Task.FromResult(ETagCacheEntry<string>.WithValue("value", "\"etag\"")),
                ifNoneMatch,
                _defaultSettings);

            // Assert
            result.Should().NotBeNull();
            result.Status.Should().Be(ETagCacheStatus.NotModified);
            result.ShouldReturn304.Should().BeTrue();
        }

        [Fact]
        public void GetETagKey_ShouldPrefixWithEtag()
        {
            // This test verifies the internal method behavior indirectly
            // by checking that ETag keys are properly prefixed in cache operations
            
            // Arrange
            var key = "test-key";
            var expectedETagKey = $"etag:{key}";
            
            _mockHybridCache
                .Setup(x => x.GetFromL1Async<string>(expectedETagKey))
                .ReturnsAsync("\"test-etag\"")
                .Verifiable();

            // Act
            var task = _cacheManager.GetOrCreateWithETagAsync(
                key,
                () => Task.FromResult(ETagCacheEntry<string>.WithValue("value", "\"etag\"")),
                null,
                _defaultSettings);

            // Assert
            _mockHybridCache.Verify(x => x.GetFromL1Async<string>(expectedETagKey), Times.Once);
        }
    }
}