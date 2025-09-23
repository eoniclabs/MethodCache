using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Providers.Redis.Configuration;
using StackExchange.Redis;
using System.Text.Json;

namespace MethodCache.Providers.Redis.Infrastructure;

/// <summary>
/// Redis implementation of IBackplane for cross-instance cache coordination.
/// Provides distributed invalidation messaging using Redis pub/sub.
/// </summary>
public class RedisBackplane : IBackplane
{
    private readonly IRedisConnectionManager _connectionManager;
    private readonly RedisOptions _options;
    private readonly ILogger<RedisBackplane> _logger;
    private ISubscriber? _subscriber;
    private bool _isListening;
    private bool _disposed;

    // Channel names for different message types
    private readonly string _keyInvalidationChannel;
    private readonly string _tagInvalidationChannel;
    private readonly string _clearAllChannel;

    /// <summary>
    /// Gets the instance ID for this backplane.
    /// </summary>
    public string InstanceId { get; }

    public RedisBackplane(
        IRedisConnectionManager connectionManager,
        IOptions<RedisOptions> options,
        ILogger<RedisBackplane> logger)
    {
        _connectionManager = connectionManager;
        _options = options.Value;
        _logger = logger;

        // Set instance ID
        InstanceId = Environment.MachineName + "_" + Environment.ProcessId;

        // Create channel names based on configured backplane channel
        var baseChannel = _options.BackplaneChannel;
        _keyInvalidationChannel = $"{baseChannel}:key";
        _tagInvalidationChannel = $"{baseChannel}:tag";
        _clearAllChannel = $"{baseChannel}:clear";
    }

    public async Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default)
    {
        if (_isListening)
        {
            _logger.LogWarning("Redis backplane is already listening");
            return;
        }

        try
        {
            _subscriber = _connectionManager.GetSubscriber();

            // Subscribe to key invalidation messages
            await _subscriber.SubscribeAsync(RedisChannel.Literal(_keyInvalidationChannel), async (channel, message) =>
            {
                try
                {
                    var backplaneMessage = DeserializeMessage(message!, BackplaneMessageType.KeyInvalidation);
                    if (backplaneMessage != null)
                    {
                        await onMessage(backplaneMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing key invalidation message from channel {Channel}", channel);
                }
            });

            // Subscribe to tag invalidation messages
            await _subscriber.SubscribeAsync(RedisChannel.Literal(_tagInvalidationChannel), async (channel, message) =>
            {
                try
                {
                    var backplaneMessage = DeserializeMessage(message!, BackplaneMessageType.TagInvalidation);
                    if (backplaneMessage != null)
                    {
                        await onMessage(backplaneMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing tag invalidation message from channel {Channel}", channel);
                }
            });

            // Subscribe to clear all messages
            await _subscriber.SubscribeAsync(RedisChannel.Literal(_clearAllChannel), async (channel, message) =>
            {
                try
                {
                    var backplaneMessage = DeserializeMessage(message!, BackplaneMessageType.ClearAll);
                    if (backplaneMessage != null)
                    {
                        await onMessage(backplaneMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing clear all message from channel {Channel}", channel);
                }
            });

            _isListening = true;
            _logger.LogInformation("Redis backplane started listening on channels: {KeyChannel}, {TagChannel}, {ClearChannel}",
                _keyInvalidationChannel, _tagInvalidationChannel, _clearAllChannel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting Redis backplane subscription");
            throw;
        }
    }

    public async Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_options.EnablePubSubInvalidation || _subscriber == null)
            return;

        try
        {
            var message = new BackplaneMessage
            {
                Type = BackplaneMessageType.KeyInvalidation,
                Key = key,
                InstanceId = Environment.MachineName,
                Timestamp = DateTimeOffset.UtcNow
            };

            var serialized = SerializeMessage(message);
            await _subscriber.PublishAsync(RedisChannel.Literal(_keyInvalidationChannel), serialized);

            _logger.LogDebug("Published key invalidation for {Key} to Redis backplane", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing key invalidation for {Key}", key);
        }
    }

    public async Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (!_options.EnablePubSubInvalidation || _subscriber == null)
            return;

        try
        {
            var message = new BackplaneMessage
            {
                Type = BackplaneMessageType.TagInvalidation,
                Tag = tag,
                InstanceId = Environment.MachineName,
                Timestamp = DateTimeOffset.UtcNow
            };

            var serialized = SerializeMessage(message);
            await _subscriber.PublishAsync(RedisChannel.Literal(_tagInvalidationChannel), serialized);

            _logger.LogDebug("Published tag invalidation for {Tag} to Redis backplane", tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing tag invalidation for {Tag}", tag);
        }
    }

    public async Task PublishClearAllAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.EnablePubSubInvalidation || _subscriber == null)
            return;

        try
        {
            var message = new BackplaneMessage
            {
                Type = BackplaneMessageType.ClearAll,
                InstanceId = Environment.MachineName,
                Timestamp = DateTimeOffset.UtcNow
            };

            var serialized = SerializeMessage(message);
            await _subscriber.PublishAsync(RedisChannel.Literal(_clearAllChannel), serialized);

            _logger.LogDebug("Published clear all to Redis backplane");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error publishing clear all message");
        }
    }

    public async Task UnsubscribeAsync(CancellationToken cancellationToken = default)
    {
        if (!_isListening || _subscriber == null)
            return;

        try
        {
            // Unsubscribe from all channels
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_keyInvalidationChannel));
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_tagInvalidationChannel));
            await _subscriber.UnsubscribeAsync(RedisChannel.Literal(_clearAllChannel));

            _isListening = false;
            _logger.LogInformation("Redis backplane stopped listening");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping Redis backplane");
        }
    }


    private string SerializeMessage(BackplaneMessage message)
    {
        return JsonSerializer.Serialize(message, JsonSerializerOptions.Default);
    }

    private BackplaneMessage? DeserializeMessage(string message, BackplaneMessageType expectedType)
    {
        try
        {
            var backplaneMessage = JsonSerializer.Deserialize<BackplaneMessage>(message);

            // Validate message type matches expected channel
            if (backplaneMessage?.Type != expectedType)
            {
                _logger.LogWarning("Received message type {ActualType} on channel for {ExpectedType}",
                    backplaneMessage?.Type, expectedType);
                return null;
            }

            return backplaneMessage;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error deserializing backplane message: {Message}", message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            // Use async disposal but don't wait indefinitely
            var stopTask = UnsubscribeAsync();
            if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("Redis backplane disposal timed out");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Redis backplane disposal");
        }
        finally
        {
            _disposed = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        try
        {
            await UnsubscribeAsync();
        }
        finally
        {
            _disposed = true;
        }
    }
}

