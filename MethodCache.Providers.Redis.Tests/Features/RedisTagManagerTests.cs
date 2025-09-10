using Xunit;
using NSubstitute;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Providers.Redis.Configuration;
using MethodCache.Providers.Redis.Features;
using StackExchange.Redis;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Tests.Features
{
    public class RedisTagManagerTests
    {
        private readonly IRedisConnectionManager _connectionManagerMock;
        private readonly IDatabase _databaseMock;
        private readonly ILogger<RedisTagManager> _loggerMock;
        private readonly RedisOptions _options;
        private readonly RedisTagManager _tagManager;

        public RedisTagManagerTests()
        {
            _connectionManagerMock = Substitute.For<IRedisConnectionManager>();
            _databaseMock = Substitute.For<IDatabase>();
            _loggerMock = Substitute.For<ILogger<RedisTagManager>>();
            
            _options = new RedisOptions 
            { 
                KeyPrefix = "test:" 
            };

            _connectionManagerMock.GetDatabase().Returns(_databaseMock);

            _tagManager = new RedisTagManager(
                _connectionManagerMock,
                Options.Create(_options),
                _loggerMock);
        }

        [Fact]
        public async Task AssociateTagsAsync_WithValidTags_CallsSetAddForEachTag()
        {
            // Arrange
            var key = "test-key";
            var tags = new[] { "tag1", "tag2", "tag3" };

            // Act
            await _tagManager.AssociateTagsAsync(key, tags);

            // Assert
            foreach (var tag in tags)
            {
                _databaseMock.Received(1).SetAddAsync($"test:tags:{tag}", key, CommandFlags.None);
            }
        }

        [Fact]
        public async Task AssociateTagsAsync_WithEmptyTags_DoesNotCallDatabase()
        {
            // Arrange
            var key = "test-key";
            var tags = Array.Empty<string>();

            // Act
            await _tagManager.AssociateTagsAsync(key, tags);

            // Assert
            _databaseMock.DidNotReceive().SetAddAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        }

        [Fact]
        public async Task GetKeysByTagsAsync_WithValidTags_ReturnsAggregatedKeys()
        {
            // Arrange
            var tags = new[] { "tag1", "tag2" };
            var tag1Keys = new RedisValue[] { "key1", "key2" };
            var tag2Keys = new RedisValue[] { "key2", "key3" };

            _databaseMock.SetMembersAsync("test:tags:tag1", CommandFlags.None)
                         .Returns(tag1Keys);
            _databaseMock.SetMembersAsync("test:tags:tag2", CommandFlags.None)
                         .Returns(tag2Keys);

            // Act
            var result = await _tagManager.GetKeysByTagsAsync(tags);

            // Assert
            Assert.Contains("key1", result);
            Assert.Contains("key2", result);
            Assert.Contains("key3", result);
            Assert.Equal(3, result.Length);
        }

        [Fact]
        public async Task GetKeysByTagsAsync_WithEmptyTags_ReturnsEmptyArray()
        {
            // Act
            var result = await _tagManager.GetKeysByTagsAsync(Array.Empty<string>());

            // Assert
            Assert.Empty(result);
            _databaseMock.DidNotReceive().SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        }

        [Fact]
        public async Task RemoveTagAssociationsAsync_WithValidKeysAndTags_CallsSetRemoveForEach()
        {
            // Arrange
            var keys = new[] { "key1", "key2" };
            var tags = new[] { "tag1", "tag2" };

            // Act
            await _tagManager.RemoveTagAssociationsAsync(keys, tags);

            // Assert
            foreach (var key in keys)
            {
                foreach (var tag in tags)
                {
                    _databaseMock.Received(1).SetRemoveAsync($"test:tags:{tag}", key, CommandFlags.None);
                }
            }
        }

        [Fact]
        public async Task RemoveTagAssociationsAsync_WithEmptyKeysOrTags_DoesNotCallDatabase()
        {
            // Act
            await _tagManager.RemoveTagAssociationsAsync(Array.Empty<string>(), new[] { "tag1" });
            await _tagManager.RemoveTagAssociationsAsync(new[] { "key1" }, Array.Empty<string>());

            // Assert
            _databaseMock.DidNotReceive().SetRemoveAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        }
    }
}