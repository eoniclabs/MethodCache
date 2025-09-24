using Xunit;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using MethodCache.Providers.Redis.Infrastructure;
using StackExchange.Redis;
using System.Threading.Tasks;
using System.Linq;

namespace MethodCache.Providers.Redis.Tests
{
    public class RedisStorageProviderTests
    {
        private readonly IRedisConnectionManager _connectionManagerMock;
        private readonly IDatabase _databaseMock;
        private readonly ITransaction _transactionMock;
        private readonly IRedisSerializer _serializerMock;
        private readonly IRedisTagManager _tagManagerMock;
        private readonly IBackplane _backplaneMock;
        private readonly ILogger<RedisStorageProvider> _loggerMock;
        private readonly RedisOptions _options;
        private readonly RedisStorageProvider _storageProvider;

        public RedisStorageProviderTests()
        {
            _connectionManagerMock = Substitute.For<IRedisConnectionManager>();
            _databaseMock = Substitute.For<IDatabase>();
            _transactionMock = Substitute.For<ITransaction>();
            _serializerMock = Substitute.For<IRedisSerializer>();
            _tagManagerMock = Substitute.For<IRedisTagManager>();
            _backplaneMock = Substitute.For<IBackplane>();
            _loggerMock = Substitute.For<ILogger<RedisStorageProvider>>();

            _options = new RedisOptions
            {
                KeyPrefix = "test:",
                EnablePubSubInvalidation = true
            };

            _connectionManagerMock.GetDatabase().Returns(_databaseMock);
            _databaseMock.CreateTransaction().Returns(_transactionMock);
            _transactionMock.ExecuteAsync().Returns(true);

            // Mock transaction operations to return completed tasks
            _transactionMock.KeyDeleteAsync(Arg.Any<RedisKey[]>())
                           .Returns(Task.FromResult(1L));
            _databaseMock.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>())
                           .Returns(Task.FromResult(true));

            _storageProvider = new RedisStorageProvider(
                _connectionManagerMock,
                _serializerMock,
                _tagManagerMock,
                _backplaneMock,
                Options.Create(_options),
                _loggerMock);
        }

        [Fact]
        public async Task GetAsync_WithCacheHit_ReturnsCachedValue()
        {
            // Arrange
            var cacheKey = "test-key";
            var fullKey = "test:" + cacheKey;
            var cachedValue = "cached result";

            _databaseMock.StringGetAsync(fullKey).Returns((RedisValue)new byte[] { 1, 2, 3 });
            _serializerMock.DeserializeAsync<string>(Arg.Any<byte[]>()).Returns(cachedValue);

            // Act
            var result = await _storageProvider.GetAsync<string>(cacheKey);

            // Assert
            Assert.Equal(cachedValue, result);
            await _databaseMock.Received(1).StringGetAsync(fullKey);
            await _serializerMock.Received(1).DeserializeAsync<string>(Arg.Any<byte[]>());
        }

        [Fact]
        public async Task GetAsync_WithCacheMiss_ReturnsDefault()
        {
            // Arrange
            var cacheKey = "missing-key";
            var fullKey = "test:" + cacheKey;

            _databaseMock.StringGetAsync(fullKey).Returns(RedisValue.Null);

            // Act
            var result = await _storageProvider.GetAsync<string>(cacheKey);

            // Assert
            Assert.Null(result);
            await _databaseMock.Received(1).StringGetAsync(fullKey);
        }

        [Fact]
        public async Task SetAsync_WithoutTags_StoresValueCorrectly()
        {
            // Arrange
            var cacheKey = "test-key";
            var fullKey = "test:" + cacheKey;
            var value = "test value";
            var expiration = TimeSpan.FromMinutes(10);
            var serializedData = new byte[] { 1, 2, 3 };

            _serializerMock.SerializeAsync(value).Returns(serializedData);
            _databaseMock.StringSetAsync(fullKey, serializedData, expiration).Returns(true);

            // Act
            await _storageProvider.SetAsync(cacheKey, value, expiration);

            // Assert
            await _serializerMock.Received(1).SerializeAsync(value);
            await _databaseMock.Received(1).StringSetAsync(fullKey, serializedData, expiration);
        }

        [Fact]
        public async Task SetAsync_WithTags_UsesAtomicLuaScript()
        {
            // Arrange
            var cacheKey = "test-key";
            var fullKey = "test:" + cacheKey;
            var value = "test value";
            var expiration = TimeSpan.FromMinutes(10);
            var tags = new[] { "tag1", "tag2" };
            var serializedData = new byte[] { 1, 2, 3 };

            _serializerMock.SerializeAsync(value).Returns(serializedData);
            _databaseMock.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
                         .Returns(Task.FromResult(RedisResult.Create(1)));

            // Act
            await _storageProvider.SetAsync(cacheKey, value, expiration, tags);

            // Assert
            await _serializerMock.Received(1).SerializeAsync(value);
            await _databaseMock.Received(1).ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>());
        }

        [Fact]
        public async Task RemoveByTagAsync_WithKeysFound_DeletesKeysAndPublishesBackplaneMessage()
        {
            // Arrange
            var tag = "test-tag";
            var keys = new[] { "key1", "key2", "key3" };

            _tagManagerMock.GetKeysByTagsAsync(Arg.Is<string[]>(tags => tags.Length == 1 && tags[0] == tag)).Returns(keys);
            _databaseMock.CreateTransaction().Returns(_transactionMock);
            _transactionMock.ExecuteAsync().Returns(true);
            _transactionMock.KeyDeleteAsync(Arg.Any<RedisKey[]>()).Returns(Task.FromResult(3L));

            // Act
            await _storageProvider.RemoveByTagAsync(tag);

            // Assert
            await _tagManagerMock.Received(1).GetKeysByTagsAsync(Arg.Is<string[]>(tags => tags.Length == 1 && tags[0] == tag));
            _databaseMock.Received(1).CreateTransaction();
            await _backplaneMock.Received(1).PublishTagInvalidationAsync(tag, Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RemoveByTagAsync_WithNoKeysFound_DoesNotCreateTransaction()
        {
            // Arrange
            var tag = "test-tag";
            var keys = new string[0]; // No keys found

            _tagManagerMock.GetKeysByTagsAsync(Arg.Is<string[]>(tags => tags.Length == 1 && tags[0] == tag)).Returns(keys);

            // Act
            await _storageProvider.RemoveByTagAsync(tag);

            // Assert
            await _tagManagerMock.Received(1).GetKeysByTagsAsync(Arg.Is<string[]>(tags => tags.Length == 1 && tags[0] == tag));
            _databaseMock.DidNotReceive().CreateTransaction();
            await _backplaneMock.DidNotReceive().PublishTagInvalidationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task RemoveAsync_RemovesKeyAndTagAssociations()
        {
            // Arrange
            var cacheKey = "test-key";
            var fullKey = "test:" + cacheKey;

            _databaseMock.KeyDeleteAsync(fullKey).Returns(true);

            // Act
            await _storageProvider.RemoveAsync(cacheKey);

            // Assert
            await _databaseMock.Received(1).KeyDeleteAsync(fullKey);
            await _tagManagerMock.Received(1).RemoveAllTagAssociationsAsync(fullKey);
        }

        [Fact]
        public async Task ExistsAsync_ChecksKeyExistence()
        {
            // Arrange
            var cacheKey = "test-key";
            var fullKey = "test:" + cacheKey;

            _databaseMock.KeyExistsAsync(fullKey).Returns(true);

            // Act
            var result = await _storageProvider.ExistsAsync(cacheKey);

            // Assert
            Assert.True(result);
            await _databaseMock.Received(1).KeyExistsAsync(fullKey);
        }
    }
}
