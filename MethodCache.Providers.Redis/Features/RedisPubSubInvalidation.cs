using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Providers.Redis.Configuration;
using StackExchange.Redis;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace MethodCache.Providers.Redis.Features
{
    public class RedisPubSubInvalidation : IRedisPubSubInvalidation, IDisposable
    {
        private readonly IRedisConnectionManager _connectionManager;
        private readonly RedisOptions _options;
        private readonly ILogger<RedisPubSubInvalidation> _logger;
        private readonly string _instanceId;
        private readonly string _channelName;
        private ISubscriber? _subscriber;
        private bool _isListening;
        private bool _disposed;

        public event EventHandler<CacheInvalidationEventArgs>? InvalidationReceived;

        public RedisPubSubInvalidation(
            IRedisConnectionManager connectionManager,
            IOptions<RedisOptions> options,
            ILogger<RedisPubSubInvalidation> logger)
        {
            _connectionManager = connectionManager;
            _options = options.Value;
            _logger = logger;
            _instanceId = Environment.MachineName + "-" + Environment.ProcessId;
            _channelName = $"{_options.KeyPrefix}invalidation";
        }

        public async Task PublishInvalidationEventAsync(string[] tags)
        {
            if (_disposed || !_options.EnablePubSubInvalidation || !tags.Any())
                return;

            try
            {
                _subscriber ??= _connectionManager.GetSubscriber();

                var invalidationEvent = new InvalidationEvent
                {
                    Tags = tags,
                    SourceInstanceId = _instanceId,
                    Timestamp = DateTimeOffset.UtcNow
                };

                var json = JsonSerializer.Serialize(invalidationEvent);
                await _subscriber.PublishAsync(RedisChannel.Literal(_channelName), json);

                _logger.LogDebug("Published invalidation event for tags {Tags} from instance {InstanceId}", 
                    string.Join(", ", tags), _instanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing invalidation event for tags {Tags}", string.Join(", ", tags));
            }
        }

        public async Task StartListeningAsync()
        {
            if (_disposed || !_options.EnablePubSubInvalidation || _isListening)
                return;

            try
            {
                _subscriber = _connectionManager.GetSubscriber();
                await _subscriber.SubscribeAsync(RedisChannel.Literal(_channelName), OnInvalidationMessageReceived);
                _isListening = true;

                _logger.LogInformation("Started listening for cache invalidation events on channel {Channel} for instance {InstanceId}", 
                    _channelName, _instanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting pub/sub invalidation listener");
            }
        }

        public async Task StopListeningAsync()
        {
            if (_disposed || !_isListening || _subscriber == null)
                return;

            try
            {
                await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_channelName));
                _isListening = false;

                _logger.LogInformation("Stopped listening for cache invalidation events on channel {Channel} for instance {InstanceId}", 
                    _channelName, _instanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping pub/sub invalidation listener");
            }
        }

        private void OnInvalidationMessageReceived(RedisChannel channel, RedisValue message)
        {
            try
            {
                var json = message.ToString();
                var invalidationEvent = JsonSerializer.Deserialize<InvalidationEvent>(json);

                if (invalidationEvent == null)
                {
                    _logger.LogWarning("Received invalid invalidation event message: {Message}", json);
                    return;
                }

                // Ignore events from this instance to prevent infinite loops
                if (invalidationEvent.SourceInstanceId == _instanceId)
                {
                    _logger.LogDebug("Ignoring invalidation event from same instance {InstanceId}", _instanceId);
                    return;
                }

                var eventArgs = new CacheInvalidationEventArgs(
                    invalidationEvent.Tags,
                    invalidationEvent.SourceInstanceId,
                    invalidationEvent.Timestamp);

                InvalidationReceived?.Invoke(this, eventArgs);

                _logger.LogDebug("Received and processed invalidation event for tags {Tags} from instance {SourceInstance}", 
                    string.Join(", ", invalidationEvent.Tags), invalidationEvent.SourceInstanceId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing invalidation message: {Message}", message.ToString());
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                StopListeningAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during disposal of pub/sub invalidation service");
            }

            _disposed = true;
        }

        private class InvalidationEvent
        {
            public string[] Tags { get; set; } = Array.Empty<string>();
            public string SourceInstanceId { get; set; } = string.Empty;
            public DateTimeOffset Timestamp { get; set; }
        }
    }
}