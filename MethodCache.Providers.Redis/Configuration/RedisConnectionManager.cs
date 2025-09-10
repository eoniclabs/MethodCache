using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Configuration
{
    public interface IRedisConnectionManager
    {
        IDatabase GetDatabase();
        ISubscriber GetSubscriber();
        Task<bool> IsConnectedAsync();
    }

    public class RedisConnectionManager : IRedisConnectionManager, IDisposable
    {
        private readonly IConnectionMultiplexer _connection;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisConnectionManager> _logger;

        public RedisConnectionManager(IConnectionMultiplexer connection, IOptions<RedisOptions> options, ILogger<RedisConnectionManager> logger)
        {
            _connection = connection;
            _options = options.Value;
            _logger = logger;
        }

        public IDatabase GetDatabase()
        {
            return _connection.GetDatabase(_options.DatabaseNumber);
        }

        public ISubscriber GetSubscriber()
        {
            return _connection.GetSubscriber();
        }

        public async Task<bool> IsConnectedAsync()
        {
            try
            {
                var database = GetDatabase();
                await database.PingAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Redis connection health check failed");
                return false;
            }
        }

        public void Dispose()
        {
            _connection?.Dispose();
        }
    }
}