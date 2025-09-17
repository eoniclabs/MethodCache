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
            var keyTagsKey = GetKeyTagsKey(key);

            foreach (var tag in tags)
            {
                var tagKey = GetTagKey(tag);
                // Add key to tag set
                tasks.Add(database.SetAddAsync(tagKey, key));
                // Add tag to key's tag set (for reverse lookup)
                tasks.Add(database.SetAddAsync(keyTagsKey, tag));
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
                    // Remove key from tag set
                    tasks.Add(database.SetRemoveAsync(tagKey, key));
                    // Remove tag from key's tag set
                    var keyTagsKey = GetKeyTagsKey(key);
                    tasks.Add(database.SetRemoveAsync(keyTagsKey, tag));
                }
            }

            await Task.WhenAll(tasks);
            _logger.LogDebug("Removed associations for {KeyCount} keys and {TagCount} tags", keys.Count(), tags.Length);
        }

        public async Task RemoveAllTagAssociationsAsync(string key)
        {
            var database = _connectionManager.GetDatabase();
            var keyTagsKey = GetKeyTagsKey(key);

            // Get all tags associated with this key
            var associatedTags = await database.SetMembersAsync(keyTagsKey);
            
            if (!associatedTags.Any())
            {
                _logger.LogDebug("No tag associations found for key {Key}", key);
                return;
            }

            var tasks = new List<Task>();
            
            // Remove the key from each tag set
            foreach (var tag in associatedTags)
            {
                var tagKey = GetTagKey(tag!);
                tasks.Add(database.SetRemoveAsync(tagKey, key));
            }
            
            // Remove the key's tag association set
            tasks.Add(database.KeyDeleteAsync(keyTagsKey));

            await Task.WhenAll(tasks);
            _logger.LogDebug("Removed all tag associations for key {Key} from {TagCount} tags", key, associatedTags.Length);
        }

        private string GetTagKey(string tag)
        {
            return $"{_options.KeyPrefix}tags:{tag}";
        }

        private string GetKeyTagsKey(string key)
        {
            return $"{_options.KeyPrefix}key-tags:{key}";
        }
    }
}