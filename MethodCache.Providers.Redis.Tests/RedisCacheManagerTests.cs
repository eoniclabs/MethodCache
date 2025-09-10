using Xunit;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Providers.Redis;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using StackExchange.Redis;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Tests
{
    public class RedisCacheManagerTests
    {
        private readonly IRedisConnectionManager _connectionManagerMock;
        private readonly IDatabase _databaseMock;
        private readonly IRedisSerializer _serializerMock;
        private readonly IRedisTagManager _tagManagerMock;
        private readonly IDistributedLock _distributedLockMock;
        private readonly IRedisPubSubInvalidation _pubSubInvalidationMock;
        private readonly ICacheMetricsProvider _metricsProviderMock;
        private readonly ICacheKeyGenerator _keyGeneratorMock;
        private readonly ILogger<RedisCacheManager> _loggerMock;
        private readonly RedisOptions _options;
        private readonly RedisCacheManager _cacheManager;

        public RedisCacheManagerTests()
        {
            _connectionManagerMock = Substitute.For<IRedisConnectionManager>();
            _databaseMock = Substitute.For<IDatabase>();
            _serializerMock = Substitute.For<IRedisSerializer>();
            _tagManagerMock = Substitute.For<IRedisTagManager>();
            _distributedLockMock = Substitute.For<IDistributedLock>();
            _pubSubInvalidationMock = Substitute.For<IRedisPubSubInvalidation>();
            _metricsProviderMock = Substitute.For<ICacheMetricsProvider>();
            _keyGeneratorMock = Substitute.For<ICacheKeyGenerator>();
            _loggerMock = Substitute.For<ILogger<RedisCacheManager>>();

            _options = new RedisOptions
            {
                KeyPrefix = "test:",
                EnableDistributedLocking = true
            };

            _connectionManagerMock.GetDatabase().Returns(_databaseMock);

            _cacheManager = new RedisCacheManager(
                _connectionManagerMock,
                _serializerMock,
                _tagManagerMock,
                _distributedLockMock,
                _pubSubInvalidationMock,
                _metricsProviderMock,
                Options.Create(_options),
                _loggerMock);
        }

        [Fact]
        public async Task GetOrCreateAsync_WithCacheHit_ReturnsCachedValue()
        {
            // Arrange
            var methodName = "TestMethod";
            var args = new object[] { 1, "test" };
            var cachedValue = "cached result";
            var cacheKey = "generated-key";
            var fullKey = "test:" + cacheKey;
            var settings = new CacheMethodSettings();

            _keyGeneratorMock.GenerateKey(methodName, args, settings).Returns(cacheKey);
            _databaseMock.StringGetAsync(fullKey, CommandFlags.None).Returns(new RedisValue("serialized-data"));
            _serializerMock.DeserializeAsync<string>(Arg.Any<byte[]>()).Returns(cachedValue);

            // Act
            var result = await _cacheManager.GetOrCreateAsync(
                methodName,
                args,
                () => Task.FromResult("factory result"),
                settings,
                _keyGeneratorMock,
                false);

            // Assert
            Assert.Equal(cachedValue, result);
            _metricsProviderMock.Received(1).CacheHit(methodName);
        }

        [Fact]
        public async Task GetOrCreateAsync_WithCacheMiss_ExecutesFactoryAndCaches()
        {
            // Arrange
            var methodName = "TestMethod";
            var args = new object[] { 1, "test" };
            var factoryResult = "factory result";
            var cacheKey = "generated-key";
            var fullKey = "test:" + cacheKey;
            var settings = new CacheMethodSettings { Tags = new List<string> { "tag1" }, Duration = TimeSpan.FromMinutes(10) };
            var lockHandle = Substitute.For<ILockHandle>();

            _keyGeneratorMock.GenerateKey(methodName, args, settings).Returns(cacheKey);
            _databaseMock.StringGetAsync(fullKey, CommandFlags.None).Returns(RedisValue.Null);
            
            lockHandle.IsAcquired.Returns(true);
            _distributedLockMock.AcquireAsync($"lock:{fullKey}", TimeSpan.FromSeconds(30), default)
                               .Returns(lockHandle);

            _serializerMock.SerializeAsync(factoryResult).Returns(new byte[] { 1, 2, 3 });
            _databaseMock.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>()).Returns(true);

            // Act
            var result = await _cacheManager.GetOrCreateAsync(
                methodName,
                args,
                () => Task.FromResult(factoryResult),
                settings,
                _keyGeneratorMock,
                false);

            // Assert
            Assert.Equal(factoryResult, result);
            _metricsProviderMock.Received(1).CacheMiss(methodName);
            
            // Debug: Verify that the distributed lock was actually called
            _distributedLockMock.Received(1).AcquireAsync($"lock:{fullKey}", TimeSpan.FromSeconds(30), default);
            
            // Debug: Verify serializer was called  
            _serializerMock.Received(1).SerializeAsync(factoryResult);
            
            // Debug: Verify tag manager was called
            _tagManagerMock.Received(1).AssociateTagsAsync(fullKey, settings.Tags);
            
            // This should be the last assertion to debug
            _databaseMock.Received(1).StringSetAsync(fullKey, Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>());
        }

        [Fact]
        public async Task InvalidateByTagsAsync_WithValidTags_DeletesKeysAndRemovesAssociations()
        {
            // Arrange
            var tags = new[] { "tag1", "tag2" };
            var keys = new[] { "key1", "key2", "key3" };

            _tagManagerMock.GetKeysByTagsAsync(tags).Returns(keys);

            // Act
            await _cacheManager.InvalidateByTagsAsync(tags);

            // Assert
            var expectedRedisKeys = keys.Select(k => (RedisKey)k).ToArray();
            _databaseMock.Received(1).KeyDeleteAsync(Arg.Is<RedisKey[]>(rk => 
                rk.Length == expectedRedisKeys.Length && 
                rk.All(key => expectedRedisKeys.Contains(key))), CommandFlags.None);
            _tagManagerMock.Received(1).RemoveTagAssociationsAsync(keys, tags);
        }

        [Fact]
        public async Task InvalidateByTagsAsync_WithEmptyTags_DoesNothing()
        {
            // Act
            await _cacheManager.InvalidateByTagsAsync();

            // Assert
            _tagManagerMock.DidNotReceive().GetKeysByTagsAsync(Arg.Any<string[]>());
            _databaseMock.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>());
        }
    }
}