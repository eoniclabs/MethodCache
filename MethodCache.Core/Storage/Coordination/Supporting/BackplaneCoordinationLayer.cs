using MethodCache.Core.Storage.Abstractions;
using MethodCache.Core.Storage.Coordination.Layers;
using MethodCache.Core.Storage.Layers.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Storage.Coordination.Supporting;

/// <summary>
/// Layer responsible for cross-instance cache invalidation coordination via backplane (e.g., Redis Pub/Sub).
/// Publishes invalidation messages and subscribes to receive messages from other instances.
/// </summary>
public sealed class BackplaneCoordinationLayer : IStorageLayer
{
    private readonly IBackplane? _backplane;
    private readonly MemoryStorageLayer _l1Layer;
    private readonly TagIndexLayer? _tagIndex;
    private readonly StorageLayerOptions _options;
    private readonly ILogger<BackplaneCoordinationLayer> _logger;
    private readonly CancellationTokenSource? _backplaneCts;
    private readonly Task? _backplaneSubscriptionTask;
    private int _disposed;

    private long _messagesSent;
    private long _messagesReceived;
    private long _invalidationsProcessed;

    public string LayerId => "Backplane";
    public int Priority => 100; // Execute last, after all storage operations
    public bool IsEnabled { get; }

    public BackplaneCoordinationLayer(
        IBackplane? backplane,
        MemoryStorageLayer l1Layer,
        IOptions<StorageLayerOptions> options,
        ILogger<BackplaneCoordinationLayer> logger,
        TagIndexLayer? tagIndex = null)
    {
        _backplane = backplane;
        _l1Layer = l1Layer ?? throw new ArgumentNullException(nameof(l1Layer));
        _tagIndex = tagIndex;
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        IsEnabled = backplane != null && _options.EnableBackplane;

        if (IsEnabled)
        {
            _backplaneCts = new CancellationTokenSource();
            _backplaneSubscriptionTask = Task.Run(async () =>
            {
                try
                {
                    await _backplane!.SubscribeAsync(OnBackplaneMessage, _backplaneCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to subscribe to backplane messages");
                }
            });
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsEnabled)
        {
            _logger.LogInformation("Initialized {LayerId} layer (InstanceId: {InstanceId})",
                LayerId, _options.InstanceId);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<StorageLayerResult<T>> GetAsync<T>(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        // Backplane doesn't handle Get operations
        return ValueTask.FromResult(StorageLayerResult<T>.NotHandled());
    }

    public ValueTask SetAsync<T>(
        StorageContext context,
        string key,
        T value,
        TimeSpan expiration,
        IEnumerable<string> tags,
        CancellationToken cancellationToken)
    {
        // Backplane doesn't handle Set operations
        // (Sets are local to this instance, no need to notify others)
        return ValueTask.CompletedTask;
    }

    public async ValueTask RemoveAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _backplane == null)
        {
            return;
        }

        // Publish key invalidation to other instances
        try
        {
            await _backplane.PublishInvalidationAsync(key, cancellationToken);
            Interlocked.Increment(ref _messagesSent);
            _logger.LogDebug("{LayerId} published invalidation for key {Key}", LayerId, key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish invalidation message for key {Key}", key);
        }
    }

    public async ValueTask RemoveByTagAsync(
        StorageContext context,
        string tag,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || _backplane == null)
        {
            return;
        }

        // Publish tag invalidation to other instances
        try
        {
            await _backplane.PublishTagInvalidationAsync(tag, cancellationToken);
            Interlocked.Increment(ref _messagesSent);
            _logger.LogDebug("{LayerId} published tag invalidation for tag {Tag}", LayerId, tag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish tag invalidation message for tag {Tag}", tag);
        }
    }

    public ValueTask<bool> ExistsAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        // Backplane doesn't handle existence checks
        return ValueTask.FromResult(false);
    }

    public ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return ValueTask.FromResult(new LayerHealthStatus(
                LayerId,
                HealthStatus.Healthy,
                "Backplane is disabled"));
        }

        var isSubscribed = _backplaneSubscriptionTask?.Status == TaskStatus.Running;
        var health = isSubscribed ? HealthStatus.Healthy : HealthStatus.Degraded;
        var message = isSubscribed
            ? "Backplane subscription is active"
            : "Backplane subscription is not active";

        var details = new Dictionary<string, object>
        {
            ["InstanceId"] = _options.InstanceId,
            ["IsSubscribed"] = isSubscribed,
            ["MessagesSent"] = Interlocked.Read(ref _messagesSent),
            ["MessagesReceived"] = Interlocked.Read(ref _messagesReceived)
        };

        return ValueTask.FromResult(new LayerHealthStatus(LayerId, health, message, details));
    }

    public LayerStats GetStats()
    {
        var additionalStats = new Dictionary<string, object>
        {
            ["MessagesSent"] = Interlocked.Read(ref _messagesSent),
            ["MessagesReceived"] = Interlocked.Read(ref _messagesReceived),
            ["InvalidationsProcessed"] = Interlocked.Read(ref _invalidationsProcessed),
            ["InstanceId"] = _options.InstanceId,
            ["Enabled"] = IsEnabled
        };

        return new LayerStats(
            LayerId,
            0, // No hits/misses for backplane
            0,
            0.0,
            Interlocked.Read(ref _messagesSent) + Interlocked.Read(ref _messagesReceived),
            additionalStats);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        var backplaneCts = _backplaneCts;
        if (backplaneCts != null)
        {
            try
            {
                backplaneCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }

            var subscriptionTask = _backplaneSubscriptionTask;
            if (subscriptionTask != null)
            {
                try
                {
                    await subscriptionTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown
                }
            }

            backplaneCts.Dispose();
        }

        if (_backplane != null)
        {
            try
            {
                await _backplane.UnsubscribeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unsubscribe from backplane");
            }
        }

        _logger.LogInformation("Disposed {LayerId} layer", LayerId);
    }

    private async Task OnBackplaneMessage(BackplaneMessage message)
    {
        try
        {
            // Ignore messages from our own instance
            if (message.InstanceId == _options.InstanceId)
            {
                return;
            }

            Interlocked.Increment(ref _messagesReceived);

            var context = new StorageContext();

            switch (message.Type)
            {
                case BackplaneMessageType.KeyInvalidation when message.Key != null:
                    await _l1Layer.RemoveAsync(context, message.Key, CancellationToken.None);
                    if (_tagIndex != null)
                    {
                        await _tagIndex.RemoveAsync(context, message.Key, CancellationToken.None);
                    }
                    Interlocked.Increment(ref _invalidationsProcessed);
                    _logger.LogDebug("Processed backplane key invalidation for {Key}", message.Key);
                    break;

                case BackplaneMessageType.TagInvalidation when message.Tag != null:
                    await _l1Layer.RemoveByTagAsync(context, message.Tag, CancellationToken.None);
                    if (_tagIndex != null)
                    {
                        await _tagIndex.RemoveByTagAsync(context, message.Tag, CancellationToken.None);
                    }
                    Interlocked.Increment(ref _invalidationsProcessed);
                    _logger.LogDebug("Processed backplane tag invalidation for {Tag}", message.Tag);
                    break;

                case BackplaneMessageType.ClearAll:
                    // Clear L1 only (don't touch L2/L3)
                    // This would require a Clear method on IStorageLayer or direct access to IMemoryStorage
                    _logger.LogDebug("Processed backplane clear all");
                    Interlocked.Increment(ref _invalidationsProcessed);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing backplane message");
        }
    }
}
