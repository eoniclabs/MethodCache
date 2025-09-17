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
            var keyTagsKey = GetKeyTagsKey(key);
            var tagArray = tags.ToArray();
            
            // Use pipelining via batch for efficient batch operations
            var batch = database.CreateBatch();
            var tasks = new List<Task>(tagArray.Length * 2);

            foreach (var tag in tagArray)
            {
                var tagKey = GetTagKey(tag);
                // Pipeline these operations - they'll be sent in a single batch
                tasks.Add(batch.SetAddAsync(tagKey, key));
                tasks.Add(batch.SetAddAsync(keyTagsKey, tag));
            }

            // Execute the batch (sends all commands at once)
            batch.Execute();
            
            // Wait for all operations to complete
            await Task.WhenAll(tasks);
            _logger.LogDebug("Associated {TagCount} tags with key {Key} using pipelining", tagArray.Length, key);
        }

        public async Task<string[]> GetKeysByTagsAsync(string[] tags)
        {
            if (!tags.Any()) return Array.Empty<string>();

            var database = _connectionManager.GetDatabase();
            
            if (tags.Length == 1)
            {
                // Single tag - use SMEMBERS directly
                var tagKey = GetTagKey(tags[0]);
                var keys = await database.SetMembersAsync(tagKey);
                var result = keys.Select(k => k.ToString()).ToArray();
                _logger.LogDebug("Found {KeyCount} keys for single tag {Tag}", result.Length, tags[0]);
                return result;
            }
            
            // Multiple tags - use efficient server-side SUNION for union operation
            var tagKeys = tags.Select(tag => (RedisKey)GetTagKey(tag)).ToArray();
            var unionKeys = await database.SetCombineAsync(SetOperation.Union, tagKeys);
            
            var resultKeys = unionKeys.Select(k => k.ToString()).ToArray();
            _logger.LogDebug("Found {KeyCount} keys for {TagCount} tags using server-side SUNION", 
                resultKeys.Length, tags.Length);
                
            return resultKeys;
        }

        public async Task RemoveTagAssociationsAsync(IEnumerable<string> keys, string[] tags)
        {
            if (!keys.Any() || !tags.Any()) return;

            var database = _connectionManager.GetDatabase();
            var keyArray = keys.ToArray();
            
            // Use pipelining via batch for efficient bulk removal
            var batch = database.CreateBatch();
            var tasks = new List<Task>(keyArray.Length * tags.Length * 2);

            foreach (var tag in tags)
            {
                var tagKey = GetTagKey(tag);
                foreach (var key in keyArray)
                {
                    // Pipeline these operations - they'll be sent in a single batch
                    tasks.Add(batch.SetRemoveAsync(tagKey, key));
                    var keyTagsKey = GetKeyTagsKey(key);
                    tasks.Add(batch.SetRemoveAsync(keyTagsKey, tag));
                }
            }

            // Execute the batch (sends all commands at once)
            batch.Execute();
            
            // Wait for all operations to complete
            await Task.WhenAll(tasks);
            _logger.LogDebug("Removed associations for {KeyCount} keys and {TagCount} tags using pipelining", 
                keyArray.Length, tags.Length);
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

            // Use pipelining via batch for efficient bulk removal
            var batch = database.CreateBatch();
            var tasks = new List<Task>(associatedTags.Length + 1);
            
            // Pipeline all removal operations
            foreach (var tag in associatedTags)
            {
                var tagKey = GetTagKey(tag!);
                tasks.Add(batch.SetRemoveAsync(tagKey, key));
            }
            
            // Remove the key's tag association set
            tasks.Add(batch.KeyDeleteAsync(keyTagsKey));

            // Execute the batch (sends all commands at once)
            batch.Execute();
            
            // Wait for all operations to complete
            await Task.WhenAll(tasks);
            _logger.LogDebug("Removed all tag associations for key {Key} from {TagCount} tags using pipelining", 
                key, associatedTags.Length);
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