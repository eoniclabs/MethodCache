using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MethodCache.Providers.Redis.Migration
{
    public class RedisCacheTarget : ICacheTarget, IDisposable
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly IDatabase _database;
        private readonly ILogger<RedisCacheTarget> _logger;
        private readonly string _keyPrefix;

        public RedisCacheTarget(
            IConnectionMultiplexer connection,
            ILogger<RedisCacheTarget> logger,
            int database = 0,
            string keyPrefix = "")
        {
            _connection = connection;
            _database = connection.GetDatabase(database);
            _logger = logger;
            _keyPrefix = keyPrefix;
        }

        public async Task SetEntryAsync(CacheEntry entry, CancellationToken cancellationToken = default)
        {
            await SetEntriesAsync(new[] { entry }, cancellationToken);
        }

        public async Task SetEntriesAsync(IEnumerable<CacheEntry> entries, CancellationToken cancellationToken = default)
        {
            var entriesList = entries.ToList();
            if (!entriesList.Any())
                return;

            try
            {
                // Use a batch for better performance
                var batch = _database.CreateBatch();
                var tasks = new List<Task>();

                foreach (var entry in entriesList)
                {
                    var redisKey = GetRedisKey(entry.Key);
                    var expiry = CalculateExpiry(entry);

                    // Set the main value
                    if (expiry.HasValue)
                    {
                        tasks.Add(batch.StringSetAsync(redisKey, entry.Value, expiry));
                    }
                    else
                    {
                        tasks.Add(batch.StringSetAsync(redisKey, entry.Value));
                    }

                    // Set tags if present
                    if (entry.Tags.Any())
                    {
                        var tagsKey = $"{redisKey}:tags";
                        var tagValues = entry.Tags.Select(t => (RedisValue)t).ToArray();
                        tasks.Add(batch.SetAddAsync(tagsKey, tagValues));
                        
                        // Set expiry for tags key if main key has expiry
                        if (expiry.HasValue)
                        {
                            tasks.Add(batch.KeyExpireAsync(tagsKey, expiry));
                        }

                        // Maintain reverse tag index for each tag
                        foreach (var tag in entry.Tags)
                        {
                            var tagIndex = $"tag:{tag}";
                            tasks.Add(batch.SetAddAsync(tagIndex, (RedisValue)redisKey.ToString()));
                            if (expiry.HasValue)
                            {
                                tasks.Add(batch.KeyExpireAsync(tagIndex, expiry));
                            }
                        }
                    }

                    // Set metadata if present
                    if (entry.Metadata.Any())
                    {
                        var metadataKey = $"{redisKey}:metadata";
                        var metadataValues = entry.Metadata
                            .Select(kvp => new HashEntry(kvp.Key, kvp.Value))
                            .ToArray();
                        
                        tasks.Add(batch.HashSetAsync(metadataKey, metadataValues));
                        
                        if (expiry.HasValue)
                        {
                            tasks.Add(batch.KeyExpireAsync(metadataKey, expiry));
                        }
                    }
                }

                batch.Execute();
                await Task.WhenAll(tasks);

                _logger.LogDebug("Successfully migrated {Count} entries", entriesList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set {Count} entries", entriesList.Count);
                throw;
            }
        }

        public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        {
            try
            {
                var redisKey = GetRedisKey(key);
                return await _database.KeyExistsAsync(redisKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check existence of key: {Key}", key);
                return false;
            }
        }

        private RedisKey GetRedisKey(string key)
        {
            return string.IsNullOrEmpty(_keyPrefix) ? key : $"{_keyPrefix}{key}";
        }

        private TimeSpan? CalculateExpiry(CacheEntry entry)
        {
            if (!entry.Expiry.HasValue)
                return null;

            var remaining = entry.Expiry.Value - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMinutes(1); // Minimum 1 minute
        }

        public void Dispose()
        {
            // Connection is typically managed externally, don't dispose it here
        }
    }

    public static class RedisCacheTargetExtensions
    {
        public static ICacheTarget AsTarget(this IConnectionMultiplexer connection,
            ILogger<RedisCacheTarget> logger,
            int database = 0,
            string keyPrefix = "")
        {
            return new RedisCacheTarget(connection, logger, database, keyPrefix);
        }
    }
}