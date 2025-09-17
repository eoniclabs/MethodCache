using System;
using System.Text.Json;
using System.Threading;
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
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _reconnectionTask;

        public event EventHandler<CacheInvalidationEventArgs>? InvalidationReceived;

        public bool IsConnected 
        { 
            get 
            {
                try 
                { 
                    return _isListening && _connectionManager.IsConnectedAsync().GetAwaiter().GetResult(); 
                } 
                catch 
                { 
                    return false; 
                }
            }
        }

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

            _cancellationTokenSource = new CancellationTokenSource();
            
            try
            {
                await StartListeningWithRetryAsync(_cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Redis backplane listener after all retry attempts");
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                throw new InvalidOperationException("Redis backplane could not be started. This will lead to inconsistent caches across instances.", ex);
            }
        }

        private async Task StartListeningWithRetryAsync(CancellationToken cancellationToken)
        {
            const int maxRetries = 5;
            var retryDelays = new[] { 1000, 2000, 4000, 8000, 16000 }; // Exponential backoff

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Redis backplane startup cancelled");
                    return;
                }

                try
                {
                    _subscriber = _connectionManager.GetSubscriber();
                    var channel = RedisChannel.Literal(_channelPrefix + "invalidation");
                    
                    // Subscribe with robust error handling
                    await _subscriber.SubscribeAsync(channel, (ch, value) =>
                    {
                        // Fire and forget with proper exception handling
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                HandleMessage(value!);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error handling backplane message, but subscription remains active");
                            }
                        }, cancellationToken);
                    });

                    _isListening = true;
                    _logger.LogInformation("Started listening to Redis backplane on channel {Channel} after {Attempt} attempts", 
                        channel, attempt + 1);

                    // Start background health monitoring and auto-reconnection
                    StartHealthMonitoring(cancellationToken);
                    return;
                }
                catch (Exception ex) when (attempt < maxRetries - 1)
                {
                    var delay = retryDelays[Math.Min(attempt, retryDelays.Length - 1)];
                    _logger.LogWarning(ex, "Failed to start Redis backplane listener (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}ms", 
                        attempt + 1, maxRetries, delay);
                    
                    await Task.Delay(delay, cancellationToken);
                }
            }
            
            throw new InvalidOperationException($"Failed to start Redis backplane after {maxRetries} attempts");
        }

        private void StartHealthMonitoring(CancellationToken cancellationToken)
        {
            _reconnectionTask = Task.Run(async () =>
            {
                const int healthCheckInterval = 30000; // 30 seconds
                
                while (!cancellationToken.IsCancellationRequested && _isListening)
                {
                    try
                    {
                        await Task.Delay(healthCheckInterval, cancellationToken);
                        
                        if (!await _connectionManager.IsConnectedAsync())
                        {
                            _logger.LogWarning("Redis connection lost, attempting to reconnect backplane");
                            
                            try
                            {
                                // Mark as not listening to trigger reconnection
                                _isListening = false;
                                
                                // Attempt to restart listening
                                await StartListeningWithRetryAsync(cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to reconnect Redis backplane during health check");
                                // Continue monitoring for next attempt
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancellation is requested
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during Redis backplane health monitoring");
                    }
                }
            }, cancellationToken);
        }

        public async Task StopListeningAsync()
        {
            if (!_isListening && _cancellationTokenSource == null) return;

            try
            {
                // Cancel health monitoring and reconnection tasks
                _cancellationTokenSource?.Cancel();

                // Unsubscribe from Redis
                if (_subscriber != null)
                {
                    var channel = RedisChannel.Literal(_channelPrefix + "invalidation");
                    await _subscriber.UnsubscribeAsync(channel);
                }

                // Wait for background tasks to complete
                if (_reconnectionTask != null)
                {
                    try
                    {
                        await _reconnectionTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when we cancelled the token
                    }
                }

                _isListening = false;
                _logger.LogInformation("Stopped listening to Redis backplane");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping Redis backplane listener");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                _reconnectionTask = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                // Use a timeout to avoid blocking indefinitely during disposal
                var stopTask = StopListeningAsync();
                if (!stopTask.Wait(TimeSpan.FromSeconds(10)))
                {
                    _logger.LogWarning("StopListeningAsync timed out during disposal, forcing cleanup");
                    
                    // Force cleanup if graceful shutdown times out
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();
                }
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

        private void HandleMessage(RedisValue value)
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

                // Raise event directly (we're already on a background task from the subscription handler)
                InvalidationReceived?.Invoke(this, eventArgs);
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