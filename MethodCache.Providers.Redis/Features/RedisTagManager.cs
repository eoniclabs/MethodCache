using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Providers.Redis.Configuration;
using StackExchange.Redis;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    public class RedisTagManager : IRedisTagManager
    {
        private readonly IRedisConnectionManager _connectionManager;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisTagManager> _logger;

        public RedisTagManager(
            IRedisConnectionManager connectionManager,
            IOptions<RedisOptions> options,
            ILogger<RedisTagManager> logger)
        {
            _connectionManager = connectionManager;
            _options = options.Value;
            _logger = logger;
        }

        public async Task AssociateTagsAsync(string key, IEnumerable<string> tags)
        {
            if (!tags.Any()) return;

            var database = _connectionManager.GetDatabase();
            var tasks = new List<Task>();

            foreach (var tag in tags)
            {
                var tagKey = GetTagKey(tag);
                tasks.Add(database.SetAddAsync(tagKey, key));
            }

            await Task.WhenAll(tasks);
            _logger.LogDebug("Associated {TagCount} tags with key {Key}", tags.Count(), key);
        }

        public async Task<string[]> GetKeysByTagsAsync(string[] tags)
        {
            if (!tags.Any()) return Array.Empty<string>();

            var database = _connectionManager.GetDatabase();
            var allKeys = new HashSet<string>();

            foreach (var tag in tags)
            {
                var tagKey = GetTagKey(tag);
                var keys = await database.SetMembersAsync(tagKey);
                
                foreach (var key in keys)
                {
                    allKeys.Add(key!);
                }
            }

            _logger.LogDebug("Found {KeyCount} keys for {TagCount} tags", allKeys.Count, tags.Length);
            return allKeys.ToArray();
        }

        public async Task RemoveTagAssociationsAsync(IEnumerable<string> keys, string[] tags)
        {
            if (!keys.Any() || !tags.Any()) return;

            var database = _connectionManager.GetDatabase();
            var tasks = new List<Task>();

            foreach (var tag in tags)
            {
                var tagKey = GetTagKey(tag);
                foreach (var key in keys)
                {
                    tasks.Add(database.SetRemoveAsync(tagKey, key));
                }
            }

            await Task.WhenAll(tasks);
            _logger.LogDebug("Removed associations for {KeyCount} keys and {TagCount} tags", keys.Count(), tags.Length);
        }

        public async Task RemoveAllTagAssociationsAsync(string key)
        {
            // This is a simplified implementation
            // In a production system, you might want to track which tags a key belongs to
            // For now, we'll rely on the expiration of the tag sets themselves
            _logger.LogDebug("Removing all tag associations for key {Key}", key);
            await Task.CompletedTask;
        }

        private string GetTagKey(string tag)
        {
            return $"{_options.KeyPrefix}tags:{tag}";
        }
    }
}