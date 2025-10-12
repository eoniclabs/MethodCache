using System.Threading.Channels;
using MethodCache.Core.Storage.Coordination.Layers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MethodCache.Core.Storage.Coordination.Supporting;

/// <summary>
/// Layer that provides async write queuing for expensive operations.
/// Wraps other layers and schedules their operations on a background worker.
/// </summary>
public sealed class AsyncWriteQueueLayer : IStorageLayer
{
    private readonly ILogger<AsyncWriteQueueLayer> _logger;
    private readonly StorageLayerOptions _options;
    private readonly Channel<Func<CancellationToken, ValueTask>>? _asyncWriteChannel;
    private readonly CancellationTokenSource? _asyncWriteCts;
    private readonly Task? _asyncWriteWorker;
    private int _disposed;

    private long _queuedWrites;
    private long _completedWrites;
    private long _failedWrites;
    private long _queueRejections;

    public string LayerId => "AsyncWriteQueue";
    public int Priority => 15; // After L1 (10), before L2 (20)
    public bool IsEnabled { get; }

    public AsyncWriteQueueLayer(
        IOptions<StorageLayerOptions> options,
        ILogger<AsyncWriteQueueLayer> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        IsEnabled = _options.EnableAsyncL2Writes || _options.EnableAsyncL3Writes;

        if (IsEnabled)
        {
            var capacity = Math.Max(1, _options.AsyncWriteQueueCapacity);
            var channelOptions = new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            };

            _asyncWriteChannel = Channel.CreateBounded<Func<CancellationToken, ValueTask>>(channelOptions);
            _asyncWriteCts = new CancellationTokenSource();
            _asyncWriteWorker = Task.Run(() => ProcessAsyncWritesAsync(_asyncWriteCts.Token));
        }
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsEnabled)
        {
            _logger.LogInformation("Initialized {LayerId} layer with capacity {Capacity}",
                LayerId, _options.AsyncWriteQueueCapacity);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<StorageLayerResult<T>> GetAsync<T>(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        // Async queue doesn't handle Get operations
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
        // Async queue layer doesn't actually set values, it just queues work
        // The actual Set operations are handled by L2/L3 layers
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        // Async queue layer doesn't handle removals directly
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveByTagAsync(
        StorageContext context,
        string tag,
        CancellationToken cancellationToken)
    {
        // Async queue layer doesn't handle removals directly
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> ExistsAsync(
        StorageContext context,
        string key,
        CancellationToken cancellationToken)
    {
        // Async queue doesn't handle existence checks
        return ValueTask.FromResult(false);
    }

    public ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return ValueTask.FromResult(new LayerHealthStatus(
                LayerId,
                HealthStatus.Healthy,
                "Async write queue is disabled"));
        }

        var queuedCount = Interlocked.Read(ref _queuedWrites) - Interlocked.Read(ref _completedWrites);
        var health = queuedCount < _options.AsyncWriteQueueCapacity * 0.9
            ? HealthStatus.Healthy
            : HealthStatus.Degraded;

        var details = new Dictionary<string, object>
        {
            ["QueuedWrites"] = queuedCount,
            ["Capacity"] = _options.AsyncWriteQueueCapacity,
            ["Utilization"] = (double)queuedCount / _options.AsyncWriteQueueCapacity
        };

        return ValueTask.FromResult(new LayerHealthStatus(
            LayerId,
            health,
            $"Queue utilization: {queuedCount}/{_options.AsyncWriteQueueCapacity}",
            details));
    }

    public LayerStats GetStats()
    {
        var additionalStats = new Dictionary<string, object>
        {
            ["QueuedWrites"] = Interlocked.Read(ref _queuedWrites),
            ["CompletedWrites"] = Interlocked.Read(ref _completedWrites),
            ["FailedWrites"] = Interlocked.Read(ref _failedWrites),
            ["QueueRejections"] = Interlocked.Read(ref _queueRejections),
            ["QueueCapacity"] = _options.AsyncWriteQueueCapacity,
            ["Enabled"] = IsEnabled
        };

        return new LayerStats(
            LayerId,
            0, // No hits/misses for async queue
            0,
            0.0,
            Interlocked.Read(ref _queuedWrites),
            additionalStats);
    }

    /// <summary>
    /// Schedules work to be executed asynchronously on the background worker.
    /// Returns true if successfully queued, false if queue is full.
    /// </summary>
    public bool TryScheduleWork(Func<CancellationToken, ValueTask> work)
    {
        if (_asyncWriteChannel == null || _asyncWriteCts == null || !IsEnabled)
        {
            return false;
        }

        if (_asyncWriteChannel.Writer.TryWrite(work))
        {
            Interlocked.Increment(ref _queuedWrites);
            return true;
        }

        Interlocked.Increment(ref _queueRejections);
        _logger.LogWarning("Async write queue is full, rejecting work");
        return false;
    }

    /// <summary>
    /// Schedules work and returns a Task that completes when the work is done.
    /// Useful for awaiting async writes.
    /// </summary>
    public bool TryScheduleAwaitableWork(Func<CancellationToken, ValueTask> work, out Task completionTask)
    {
        if (_asyncWriteChannel == null || _asyncWriteCts == null || !IsEnabled)
        {
            completionTask = Task.CompletedTask;
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_asyncWriteChannel.Writer.TryWrite(async ct =>
        {
            try
            {
                await work(ct).ConfigureAwait(false);
                tcs.TrySetResult(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            Interlocked.Increment(ref _queuedWrites);
            completionTask = tcs.Task;
            return true;
        }

        Interlocked.Increment(ref _queueRejections);
        completionTask = Task.CompletedTask;
        return false;
    }

    private async Task ProcessAsyncWritesAsync(CancellationToken cancellationToken)
    {
        if (_asyncWriteChannel == null)
        {
            return;
        }

        try
        {
            await foreach (var work in _asyncWriteChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await work(cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _completedWrites);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Graceful shutdown
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failedWrites);
                    _logger.LogError(ex, "Error executing asynchronous cache write operation");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disposing
        }

        _logger.LogInformation("{LayerId} worker stopped. Completed: {Completed}, Failed: {Failed}",
            LayerId, Interlocked.Read(ref _completedWrites), Interlocked.Read(ref _failedWrites));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _asyncWriteChannel?.Writer.TryComplete();

        var asyncWriteCts = _asyncWriteCts;
        if (asyncWriteCts != null)
        {
            try
            {
                asyncWriteCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
        }

        var asyncWriteWorker = _asyncWriteWorker;
        if (asyncWriteWorker != null)
        {
            try
            {
                await asyncWriteWorker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        asyncWriteCts?.Dispose();

        _logger.LogInformation("Disposed {LayerId} layer", LayerId);
    }
}
