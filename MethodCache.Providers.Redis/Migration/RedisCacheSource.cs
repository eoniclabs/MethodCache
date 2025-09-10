using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MethodCache.Providers.Redis.Migration
{
    public class RedisCacheSource : ICacheSource, IDisposable
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly IDatabase _database;
        private readonly IServer _server;
        private readonly ILogger<RedisCacheSource> _logger;
        private readonly string _keyPrefix;

        public RedisCacheSource(
            IConnectionMultiplexer connection,
            ILogger<RedisCacheSource> logger,
            int database = 0,
            string keyPrefix = "")
        {
            _connection = connection;
            _database = connection.GetDatabase(database);
            _server = connection.GetServers().First(); // Use first server for key scanning
            _logger = logger;
            _keyPrefix = keyPrefix;
        }

        public Task<IAsyncEnumerable<CacheEntry>> GetAllEntriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(GetAllEntriesAsyncImpl(cancellationToken));
        }

        private async IAsyncEnumerable<CacheEntry> GetAllEntriesAsyncImpl(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var pattern = string.IsNullOrEmpty(_keyPrefix) ? "*" : $"{_keyPrefix}*";
            
            _logger.LogInformation("Scanning Redis keys with pattern: {Pattern}", pattern);

            await foreach (var key in _server.KeysAsync(pattern: pattern, pageSize: 1000))
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                var entry = await GetEntryAsync(key!, cancellationToken);
                if (entry != null)
                {
                    yield return entry;
                }
            }
        }

        public async Task<long> GetTotalCountAsync(CancellationToken cancellationToken = default)
        {
            var pattern = string.IsNullOrEmpty(_keyPrefix) ? "*" : $"{_keyPrefix}*";
            var count = 0L;

            await foreach (var key in _server.KeysAsync(pattern: pattern, pageSize: 1000))
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                count++;
            }

            _logger.LogInformation("Found {Count} keys matching pattern {Pattern}", count, pattern);
            return count;
        }

        public async Task<CacheEntry?> GetEntryAsync(string key, CancellationToken cancellationToken = default)
        {
            try
            {
                var redisKey = (RedisKey)key;
                
                // Get value and TTL in a single pipeline
                var batch = _database.CreateBatch();
                var valueTask = batch.StringGetAsync(redisKey);
                var ttlTask = batch.KeyTimeToLiveAsync(redisKey);
                batch.Execute();

                var value = await valueTask;
                var ttl = await ttlTask;

                if (!value.HasValue)
                    return null;

                var entry = new CacheEntry
                {
                    Key = key,
                    Value = value!,
                    Expiry = ttl.HasValue ? DateTime.UtcNow.Add(ttl.Value) : null
                };

                // Try to get tags if they exist (assumes tags are stored in a separate structure)
                await TryLoadTagsAsync(entry, redisKey);

                return entry;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get entry for key: {Key}", key);
                return null;
            }
        }

        private async Task TryLoadTagsAsync(CacheEntry entry, RedisKey redisKey)
        {
            try
            {
                // Check if there's a tags set for this key (common pattern)
                var tagsKey = $"{redisKey}:tags";
                var tags = await _database.SetMembersAsync(tagsKey);
                
                foreach (var tag in tags)
                {
                    if (tag.HasValue)
                        entry.Tags.Add(tag!);
                }

                // Also check for metadata hash
                var metadataKey = $"{redisKey}:metadata";
                if (await _database.KeyExistsAsync(metadataKey))
                {
                    var metadata = await _database.HashGetAllAsync(metadataKey);
                    foreach (var kvp in metadata)
                    {
                        entry.Metadata[kvp.Name!] = kvp.Value!;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load tags/metadata for key: {Key}", entry.Key);
                // Non-critical, continue without tags
            }
        }

        public void Dispose()
        {
            // Connection is typically managed externally, don't dispose it here
        }
    }

    public static class RedisCacheSourceExtensions
    {
        public static ICacheSource AsSource(this IConnectionMultiplexer connection, 
            ILogger<RedisCacheSource> logger,
            int database = 0, 
            string keyPrefix = "")
        {
            return new RedisCacheSource(connection, logger, database, keyPrefix);
        }
    }
}