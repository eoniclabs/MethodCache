using System;
using System.Text.Json;
using System.Threading.Tasks;
using MethodCache.HybridCache.Abstractions;
using MethodCache.Providers.Redis.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace MethodCache.Providers.Redis.Backplane
{
    /// <summary>
    /// Redis Pub/Sub implementation of the cache backplane for hybrid caching.
    /// </summary>
    public class RedisCacheBackplane : ICacheBackplane
    {
        private readonly IRedisConnectionManager _connectionManager;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisCacheBackplane> _logger;
        private readonly string _channelPrefix;
        private readonly string _instanceId;
        private ISubscriber? _subscriber;
        private bool _isListening;
        private bool _disposed;

        public event EventHandler<CacheInvalidationEventArgs>? InvalidationReceived;

        public bool IsConnected => _connectionManager.IsConnectedAsync().GetAwaiter().GetResult();

        public RedisCacheBackplane(
            IRedisConnectionManager connectionManager,
            IOptions<RedisOptions> options,
            ILogger<RedisCacheBackplane> logger)
        {
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _options = options.Value;
            _logger = logger;
            _channelPrefix = _options.KeyPrefix + "backplane:";
            _instanceId = Guid.NewGuid().ToString();
        }

        public async Task PublishInvalidationAsync(params string[] tags)
        {
            if (tags == null || tags.Length == 0) return;

            try
            {
                var message = new BackplaneMessage
                {
                    Type = InvalidationType.ByTags,
                    Tags = tags,
                    Keys = Array.Empty<string>(),
                    SourceInstanceId = _instanceId,
                    Timestamp = DateTime.UtcNow
                };

                await PublishMessageAsync(message);
                _logger.LogDebug("Published invalidation for tags: {Tags}", string.Join(", ", tags));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing tag invalidation to backplane");
            }
        }

        public async Task PublishKeyInvalidationAsync(params string[] keys)
        {
            if (keys == null || keys.Length == 0) return;

            try
            {
                var message = new BackplaneMessage
                {
                    Type = InvalidationType.ByKeys,
                    Tags = Array.Empty<string>(),
                    Keys = keys,
                    SourceInstanceId = _instanceId,
                    Timestamp = DateTime.UtcNow
                };

                await PublishMessageAsync(message);
                _logger.LogDebug("Published invalidation for keys: {Keys}", string.Join(", ", keys));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing key invalidation to backplane");
            }
        }

        public async Task StartListeningAsync()
        {
            if (_isListening) return;

            try
            {
                _subscriber = _connectionManager.GetSubscriber();
                var channel = RedisChannel.Literal(_channelPrefix + "invalidation");
                
                await _subscriber.SubscribeAsync(channel, async (ch, value) =>
                {
                    await HandleMessageAsync(value!);
                });

                _isListening = true;
                _logger.LogInformation("Started listening to Redis backplane on channel {Channel}", channel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting Redis backplane listener");
                throw;
            }
        }

        public async Task StopListeningAsync()
        {
            if (!_isListening) return;

            try
            {
                if (_subscriber != null)
                {
                    var channel = RedisChannel.Literal(_channelPrefix + "invalidation");
                    await _subscriber.UnsubscribeAsync(channel);
                }

                _isListening = false;
                _logger.LogInformation("Stopped listening to Redis backplane");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Redis backplane listener");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                StopListeningAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during RedisCacheBackplane disposal");
            }
            finally
            {
                _disposed = true;
            }
        }

        private async Task PublishMessageAsync(BackplaneMessage message)
        {
            var subscriber = _connectionManager.GetSubscriber();
            var channel = RedisChannel.Literal(_channelPrefix + "invalidation");
            var json = JsonSerializer.Serialize(message);
            
            await subscriber.PublishAsync(channel, json);
        }

        private async Task HandleMessageAsync(RedisValue value)
        {
            try
            {
                var json = value.ToString();
                var message = JsonSerializer.Deserialize<BackplaneMessage>(json);
                
                if (message == null)
                {
                    _logger.LogWarning("Received null backplane message");
                    return;
                }

                // Skip messages from ourselves
                if (message.SourceInstanceId == _instanceId)
                {
                    return;
                }

                _logger.LogDebug("Received backplane message from {SourceInstance} of type {Type}", 
                    message.SourceInstanceId, message.Type);

                var eventArgs = new CacheInvalidationEventArgs
                {
                    Tags = message.Tags ?? Array.Empty<string>(),
                    Keys = message.Keys ?? Array.Empty<string>(),
                    SourceInstanceId = message.SourceInstanceId ?? string.Empty,
                    Timestamp = message.Timestamp,
                    Type = message.Type
                };

                // Raise event on a background task to avoid blocking the subscription
                await Task.Run(() => InvalidationReceived?.Invoke(this, eventArgs));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling backplane message");
            }
        }

        private class BackplaneMessage
        {
            public InvalidationType Type { get; set; }
            public string[]? Tags { get; set; }
            public string[]? Keys { get; set; }
            public string? SourceInstanceId { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}