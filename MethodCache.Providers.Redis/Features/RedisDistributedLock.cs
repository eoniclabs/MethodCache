using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Providers.Redis.Configuration;
using StackExchange.Redis;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    public class RedisDistributedLock : IDistributedLock
    {
        private readonly IRedisConnectionManager _connectionManager;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisDistributedLock> _logger;

        public RedisDistributedLock(
            IRedisConnectionManager connectionManager,
            IOptions<RedisOptions> options,
            ILogger<RedisDistributedLock> logger)
        {
            _connectionManager = connectionManager;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<ILockHandle> AcquireAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default)
        {
            var lockKey = GetLockKey(resource);
            var lockValue = Guid.NewGuid().ToString();
            var database = _connectionManager.GetDatabase();

            var acquired = await database.StringSetAsync(lockKey, lockValue, expiry, When.NotExists);

            if (acquired)
            {
                _logger.LogDebug("Acquired distributed lock for resource {Resource}", resource);
                return new RedisLockHandle(database, lockKey, lockValue, resource, _logger);
            }

            _logger.LogDebug("Failed to acquire distributed lock for resource {Resource}", resource);
            return new RedisLockHandle(database, lockKey, lockValue, resource, _logger, false);
        }

        private string GetLockKey(string resource)
        {
            return $"{_options.KeyPrefix}locks:{resource}";
        }
    }

    internal class RedisLockHandle : ILockHandle
    {
        private readonly IDatabase _database;
        private readonly string _lockKey;
        private readonly string _lockValue;
        private readonly ILogger _logger;
        private bool _disposed;

        public RedisLockHandle(IDatabase database, string lockKey, string lockValue, string resource, ILogger logger, bool isAcquired = true)
        {
            _database = database;
            _lockKey = lockKey;
            _lockValue = lockValue;
            Resource = resource;
            _logger = logger;
            IsAcquired = isAcquired;
        }

        public bool IsAcquired { get; private set; }
        public string Resource { get; }

        public async Task RenewAsync(TimeSpan expiry)
        {
            if (!IsAcquired || _disposed) return;

            const string script = @"
                if redis.call('GET', KEYS[1]) == ARGV[1] then
                    return redis.call('EXPIRE', KEYS[1], ARGV[2])
                else
                    return 0
                end";

            var result = await _database.ScriptEvaluateAsync(script, new RedisKey[] { _lockKey }, new RedisValue[] { _lockValue, (int)expiry.TotalSeconds });
            
            if (result.ToString() == "1")
            {
                _logger.LogDebug("Renewed distributed lock for resource {Resource}", Resource);
            }
            else
            {
                _logger.LogWarning("Failed to renew distributed lock for resource {Resource} - lock may have been lost", Resource);
                IsAcquired = false;
            }
        }

        public void Dispose()
        {
            if (_disposed || !IsAcquired) return;

            try
            {
                // Use Lua script to ensure we only delete our own lock
                const string script = @"
                    if redis.call('GET', KEYS[1]) == ARGV[1] then
                        return redis.call('DEL', KEYS[1])
                    else
                        return 0
                    end";

                _database.ScriptEvaluate(script, new RedisKey[] { _lockKey }, new RedisValue[] { _lockValue });
                _logger.LogDebug("Released distributed lock for resource {Resource}", Resource);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error releasing distributed lock for resource {Resource}", Resource);
            }
            finally
            {
                _disposed = true;
                IsAcquired = false;
            }
        }
    }
}