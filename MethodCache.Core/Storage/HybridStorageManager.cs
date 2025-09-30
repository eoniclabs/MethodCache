using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Storage;

/// <summary>
/// Manages hybrid storage with L1 (memory), L2 (distributed), and optional L3 (persistent) layers.
/// </summary>
public class HybridStorageManager : IStorageProvider, IAsyncDisposable
{
    private readonly IMemoryStorage _l1Storage;
    private readonly IStorageProvider? _l2Storage;
    private readonly IPersistentStorageProvider? _l3Storage;
    private readonly IBackplane? _backplane;
    private readonly ICacheMetricsProvider? _metricsProvider;
    private readonly StorageOptions _options;
    private readonly ILogger<HybridStorageManager> _logger;
    private readonly SemaphoreSlim _l2Semaphore;
    private readonly SemaphoreSlim _l3Semaphore;

    // Statistics
    private long _l1Hits;
    private long _l1Misses;
    private long _l2Hits;
    private long _l2Misses;
    private long _l3Hits;
    private long _l3Misses;
    private long _backplaneMessagesSent;
    private long _backplaneMessagesReceived;

    private readonly Channel<Func<CancellationToken, ValueTask>>? _asyncWriteChannel;
    private readonly CancellationTokenSource? _asyncWriteCts;
    private readonly Task? _asyncWriteWorker;
    private readonly CancellationTokenSource? _backplaneCts;
    private readonly Task? _backplaneSubscriptionTask;
    private int _disposed;

    private const string MetricsPrefix = "HybridStorage";
    private const string MetricsL1 = MetricsPrefix + ":L1";
    private const string MetricsL2 = MetricsPrefix + ":L2";
    private const string MetricsL3 = MetricsPrefix + ":L3";

    public string Name => $"Hybrid(L1+{(_l2Storage != null ? "L2+" : "")}{(_l3Storage != null ? "L3" : "Memory-Only")})";

    public HybridStorageManager(
        IMemoryStorage l1Storage,
        IOptions<StorageOptions> options,
        ILogger<HybridStorageManager> logger,
        IStorageProvider? l2Storage = null,
        IPersistentStorageProvider? l3Storage = null,
        IBackplane? backplane = null,
        ICacheMetricsProvider? metricsProvider = null)
    {
        _l1Storage = l1Storage;
        _l2Storage = l2Storage;
        _l3Storage = l3Storage;
        _backplane = backplane;
        _metricsProvider = metricsProvider;
        _options = options.Value;
        _logger = logger;
        _l2Semaphore = new SemaphoreSlim(_options.MaxConcurrentL2Operations, _options.MaxConcurrentL2Operations);
        _l3Semaphore = new SemaphoreSlim(_options.MaxConcurrentL3Operations, _options.MaxConcurrentL3Operations);

        if (_options.EnableAsyncL2Writes || _options.EnableAsyncL3Writes)
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

        if (_backplane != null)
        {
            _backplaneCts = new CancellationTokenSource();
            _backplaneSubscriptionTask = Task.Run(async () =>
            {
                try
                {
                    await _backplane.SubscribeAsync(OnBackplaneMessage, _backplaneCts.Token).ConfigureAwait(false);
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

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        // Try L1 first
        var result = await _l1Storage.GetAsync<T>(key, cancellationToken);
        if (result != null)
        {
            Interlocked.Increment(ref _l1Hits);
            _metricsProvider?.CacheHit(MetricsL1);
            _logger.LogDebug("L1 cache hit for key {Key}", key);
            return result;
        }

        Interlocked.Increment(ref _l1Misses);
        _metricsProvider?.CacheMiss(MetricsL1);

        // Try L2 if enabled
        if (_l2Storage != null && _options.L2Enabled)
        {
            try
            {
                await _l2Semaphore.WaitAsync(cancellationToken);
                result = await _l2Storage.GetAsync<T>(key, cancellationToken);

                if (result != null)
                {
                    Interlocked.Increment(ref _l2Hits);
                    _metricsProvider?.CacheHit(MetricsL2);
                    _logger.LogDebug("L2 cache hit for key {Key}", key);

                    // Warm L1 cache with shorter expiration
                    // NOTE: Tags are not preserved when promoting from L2 to L1 due to
                    // IStorageProvider interface limitations. This is acceptable because:
                    // - L1 cache has short expiration and will refresh frequently
                    // - Tag-based invalidations still work via backplane messages
                    // - Most tag operations target L2/L3 directly
                    var l1Expiration = CalculateL1Expiration(_options.L2DefaultExpiration);
                    await _l1Storage.SetAsync(key, result, l1Expiration, Enumerable.Empty<string>(), cancellationToken);

                    return result;
                }

                Interlocked.Increment(ref _l2Misses);
                _metricsProvider?.CacheMiss(MetricsL2);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing L2 storage for key {Key}", key);
                Interlocked.Increment(ref _l2Misses);
            }
            finally
            {
                _l2Semaphore.Release();
            }
        }

        // Try L3 if enabled
        if (_l3Storage != null && _options.L3Enabled)
        {
            try
            {
                await _l3Semaphore.WaitAsync(cancellationToken);
                result = await _l3Storage.GetAsync<T>(key, cancellationToken);

                if (result != null)
                {
                    Interlocked.Increment(ref _l3Hits);
                    _metricsProvider?.CacheHit(MetricsL3);
                    _logger.LogDebug("L3 cache hit for key {Key}", key);

                    // Promote to higher layers if enabled
                    if (_options.EnableL3Promotion)
                    {
                        await PromoteFromL3Async(key, result, cancellationToken);
                    }

                    return result;
                }

                Interlocked.Increment(ref _l3Misses);
                _metricsProvider?.CacheMiss(MetricsL3);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accessing L3 storage for key {Key}", key);
                Interlocked.Increment(ref _l3Misses);
            }
            finally
            {
                _l3Semaphore.Release();
            }
        }

        _logger.LogDebug("Cache miss for key {Key}", key);
        return default;
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        await SetAsync(key, value, expiration, Enumerable.Empty<string>(), cancellationToken);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var l1Expiration = CalculateL1Expiration(expiration);

        // L1 always executes
        var l1Task = _l1Storage.SetAsync(key, value, l1Expiration, tags, cancellationToken);

        // Determine which tasks to execute
        var hasL2 = _l2Storage != null && _options.L2Enabled;
        var hasL3 = _l3Storage != null && _options.L3Enabled;
        ValueTask l2Task = default;
        ValueTask l3Task = default;
        bool scheduleL2 = false, scheduleL3 = false;

        if (hasL2)
        {
            scheduleL2 = _options.EnableAsyncL2Writes && TryScheduleAsyncWrite(ct => WriteToL2Async(key, value, expiration, tags, ct));
            if (!scheduleL2)
            {
                if (_options.EnableAsyncL2Writes)
                    _logger.LogDebug("Async L2 write queue full; performing synchronous write for key {Key}", key);
                l2Task = WriteToL2Async(key, value, expiration, tags, cancellationToken);
            }
        }

        if (hasL3)
        {
            var l3Expiration = CalculateL3Expiration(expiration);
            scheduleL3 = _options.EnableAsyncL3Writes && TryScheduleAsyncWrite(ct => WriteToL3Async(key, value, l3Expiration, tags, ct));
            if (!scheduleL3)
            {
                if (_options.EnableAsyncL3Writes)
                    _logger.LogDebug("Async L3 write queue full; performing synchronous write for key {Key}", key);
                l3Task = WriteToL3Async(key, value, l3Expiration, tags, cancellationToken);
            }
        }

        // Await based on what's actually needed (avoids List allocation)
        await l1Task.ConfigureAwait(false);
        if (hasL2 && !scheduleL2) await l2Task.ConfigureAwait(false);
        if (hasL3 && !scheduleL3) await l3Task.ConfigureAwait(false);

        _logger.LogDebug("Set key {Key} in hybrid storage with expiration {Expiration}", key, expiration);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        // L1 always executes
        var l1Task = _l1Storage.RemoveAsync(key, cancellationToken);

        // Determine which tasks to execute
        var hasL2 = _l2Storage != null && _options.L2Enabled;
        var hasL3 = _l3Storage != null && _options.L3Enabled;
        ValueTask l2Task = default;
        ValueTask l3Task = default;

        if (hasL2)
        {
            l2Task = WriteToL2Async(() => _l2Storage!.RemoveAsync(key, cancellationToken), cancellationToken);
        }

        if (hasL3)
        {
            l3Task = WriteToL3Async(() => _l3Storage!.RemoveAsync(key, cancellationToken), cancellationToken);
        }

        // Await all tasks that were started
        await l1Task.ConfigureAwait(false);
        if (hasL2) await l2Task.ConfigureAwait(false);
        if (hasL3) await l3Task.ConfigureAwait(false);

        // Notify other instances via backplane
        if (_backplane != null && _options.EnableBackplane)
        {
            try
            {
                await _backplane.PublishInvalidationAsync(key, cancellationToken);
                Interlocked.Increment(ref _backplaneMessagesSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish invalidation message for key {Key}", key);
            }
        }

        _logger.LogDebug("Removed key {Key} from hybrid storage", key);
    }

    public async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        // L1 always executes
        var l1Task = _l1Storage.RemoveByTagAsync(tag, cancellationToken);

        // Determine which tasks to execute
        var hasL2 = _l2Storage != null && _options.L2Enabled;
        var hasL3 = _l3Storage != null && _options.L3Enabled;
        ValueTask l2Task = default;
        ValueTask l3Task = default;

        if (hasL2)
        {
            l2Task = WriteToL2Async(() => _l2Storage!.RemoveByTagAsync(tag, cancellationToken), cancellationToken);
        }

        if (hasL3)
        {
            l3Task = WriteToL3Async(() => _l3Storage!.RemoveByTagAsync(tag, cancellationToken), cancellationToken);
        }

        // Await all tasks that were started
        await l1Task.ConfigureAwait(false);
        if (hasL2) await l2Task.ConfigureAwait(false);
        if (hasL3) await l3Task.ConfigureAwait(false);

        // Notify other instances via backplane
        if (_backplane != null && _options.EnableBackplane)
        {
            try
            {
                await _backplane.PublishTagInvalidationAsync(tag, cancellationToken);
                Interlocked.Increment(ref _backplaneMessagesSent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish tag invalidation message for tag {Tag}", tag);
            }
        }

        _logger.LogDebug("Removed all keys with tag {Tag} from hybrid storage", tag);
    }

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        // Check L1 first (async to avoid blocking)
        var l1Result = await _l1Storage.GetAsync<object>(key, cancellationToken).ConfigureAwait(false);
        if (l1Result != null)
        {
            return true;
        }

        // Check L2 if enabled
        if (_l2Storage != null && _options.L2Enabled)
        {
            try
            {
                await _l2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                var l2Exists = await _l2Storage.ExistsAsync(key, cancellationToken).ConfigureAwait(false);
                if (l2Exists) return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking existence in L2 storage for key {Key}", key);
            }
            finally
            {
                _l2Semaphore.Release();
            }
        }

        // Check L3 if enabled
        if (_l3Storage != null && _options.L3Enabled)
        {
            try
            {
                await _l3Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                return await _l3Storage.ExistsAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking existence in L3 storage for key {Key}", key);
                return false;
            }
            finally
            {
                _l3Semaphore.Release();
            }
        }

        return false;
    }

    public async ValueTask<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var l1Healthy = true; // Memory storage is generally always healthy
        var l2Healthy = true;
        var l3Healthy = true;

        if (_l2Storage != null && _options.L2Enabled)
        {
            try
            {
                l2Healthy = await _l2Storage.GetHealthAsync(cancellationToken).ConfigureAwait(false) == HealthStatus.Healthy;
            }
            catch
            {
                l2Healthy = false;
            }
        }

        if (_l3Storage != null && _options.L3Enabled)
        {
            try
            {
                l3Healthy = await _l3Storage.GetHealthAsync(cancellationToken).ConfigureAwait(false) == HealthStatus.Healthy;
            }
            catch
            {
                l3Healthy = false;
            }
        }

        // Calculate overall health based on available layers
        var healthyLayers = 0;
        var totalLayers = 1; // L1 always exists

        if (l1Healthy) healthyLayers++;

        if (_l2Storage != null && _options.L2Enabled)
        {
            totalLayers++;
            if (l2Healthy) healthyLayers++;
        }

        if (_l3Storage != null && _options.L3Enabled)
        {
            totalLayers++;
            if (l3Healthy) healthyLayers++;
        }

        if (healthyLayers == totalLayers)
            return HealthStatus.Healthy;

        if (l1Healthy) // L1 is working, at least some functionality available
            return HealthStatus.Degraded;

        return HealthStatus.Unhealthy;
    }

    public async ValueTask<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var l1Stats = _l1Storage.GetStats();
        var l2Stats = _l2Storage != null ? await _l2Storage.GetStatsAsync(cancellationToken).ConfigureAwait(false) : null;
        var l3Stats = _l3Storage != null ? await _l3Storage.GetStatsAsync(cancellationToken).ConfigureAwait(false) : null;

        var totalHits = Interlocked.Read(ref _l1Hits) + Interlocked.Read(ref _l2Hits) + Interlocked.Read(ref _l3Hits);
        var totalMisses = Interlocked.Read(ref _l1Misses) + Interlocked.Read(ref _l2Misses) + Interlocked.Read(ref _l3Misses);

        var stats = new StorageStats
        {
            GetOperations = totalHits + totalMisses,
            SetOperations = l1Stats.EntryCount, // Approximate
            RemoveOperations = 0, // Would need to track this
            AverageResponseTimeMs = 0, // Would need to track this
            ErrorCount = 0, // Would need to track this
            AdditionalStats = new Dictionary<string, object>
            {
                ["L1Hits"] = Interlocked.Read(ref _l1Hits),
                ["L1Misses"] = Interlocked.Read(ref _l1Misses),
                ["L2Hits"] = Interlocked.Read(ref _l2Hits),
                ["L2Misses"] = Interlocked.Read(ref _l2Misses),
                ["L3Hits"] = Interlocked.Read(ref _l3Hits),
                ["L3Misses"] = Interlocked.Read(ref _l3Misses),
                ["L1HitRatio"] = l1Stats.HitRatio,
                ["L1EntryCount"] = l1Stats.EntryCount,
                ["L1Evictions"] = l1Stats.Evictions,
                ["TagMappingCount"] = l1Stats.TagMappingCount,
                ["BackplaneMessagesSent"] = Interlocked.Read(ref _backplaneMessagesSent),
                ["BackplaneMessagesReceived"] = Interlocked.Read(ref _backplaneMessagesReceived),
                ["TotalHitRatio"] = totalHits + totalMisses > 0 ? (double)totalHits / (totalHits + totalMisses) : 0.0,
                ["L2Stats"] = l2Stats ?? (object)"Not Available",
                ["L3Stats"] = l3Stats ?? (object)"Not Available"
            }
        };

        return stats;
    }

    private TimeSpan CalculateL1Expiration(TimeSpan originalExpiration)
    {
        // Always respect the original expiration if it's shorter than max
        // Only use L1DefaultExpiration as a lower bound when original expiration is very long
        var l1Expiration = TimeSpan.FromTicks(Math.Min(
            originalExpiration.Ticks,
            _options.L1MaxExpiration.Ticks));

        // Don't enforce a minimum expiration time - respect short expirations
        return l1Expiration;
    }

    private async ValueTask WriteToL2Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        if (_l2Storage == null)
            return;

        var waitSucceeded = false;
        try
        {
            await _l2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSucceeded = true;
            await _l2Storage.SetAsync(key, value, expiration, tags, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("L2 write for key {Key} was cancelled due to shutdown", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to L2 storage for key {Key}", key);
        }
        finally
        {
            if (waitSucceeded)
            {
                _l2Semaphore.Release();
            }
        }
    }

    private ValueTask WriteToL2Async(Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        if (_options.EnableAsyncL2Writes && TryScheduleAwaitableAsyncWrite(ct => ExecuteL2OperationAsync(operation, ct), out var completionTask))
        {
            return new ValueTask(WaitForScheduledWriteAsync(completionTask, cancellationToken));
        }

        return ExecuteL2OperationAsync(operation, cancellationToken);
    }

    private async ValueTask ExecuteL2OperationAsync(Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        var waitSucceeded = false;
        try
        {
            await _l2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSucceeded = true;
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("L2 operation was cancelled due to shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in L2 storage operation");
        }
        finally
        {
            if (waitSucceeded)
            {
                _l2Semaphore.Release();
            }
        }
    }

    private async Task OnBackplaneMessage(BackplaneMessage message)
    {
        try
        {
            // Ignore messages from our own instance
            if (message.InstanceId == _options.InstanceId)
                return;

            Interlocked.Increment(ref _backplaneMessagesReceived);

            switch (message.Type)
            {
                case BackplaneMessageType.KeyInvalidation when message.Key != null:
                    await _l1Storage.RemoveAsync(message.Key);
                    _logger.LogDebug("Processed backplane key invalidation for {Key}", message.Key);
                    break;

                case BackplaneMessageType.TagInvalidation when message.Tag != null:
                    await _l1Storage.RemoveByTagAsync(message.Tag);
                    _logger.LogDebug("Processed backplane tag invalidation for {Tag}", message.Tag);
                    break;

                case BackplaneMessageType.ClearAll:
                    _l1Storage.Clear();
                    _logger.LogDebug("Processed backplane clear all");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing backplane message");
        }
    }

    private TimeSpan CalculateL3Expiration(TimeSpan originalExpiration)
    {
        // L3 typically has longer expiration times
        var l3Expiration = TimeSpan.FromTicks(Math.Max(
            originalExpiration.Ticks,
            _options.L3DefaultExpiration.Ticks));

        return TimeSpan.FromTicks(Math.Min(
            l3Expiration.Ticks,
            _options.L3MaxExpiration.Ticks));
    }

    private async ValueTask WriteToL3Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        if (_l3Storage == null)
            return;

        var waitSucceeded = false;
        try
        {
            await _l3Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSucceeded = true;
            await _l3Storage.SetAsync(key, value, expiration, tags, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("L3 write for key {Key} was cancelled due to shutdown", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to L3 storage for key {Key}", key);
        }
        finally
        {
            if (waitSucceeded)
            {
                _l3Semaphore.Release();
            }
        }
    }

    private ValueTask WriteToL3Async(Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        if (_options.EnableAsyncL3Writes && TryScheduleAwaitableAsyncWrite(ct => ExecuteL3OperationAsync(operation, ct), out var completionTask))
        {
            return new ValueTask(WaitForScheduledWriteAsync(completionTask, cancellationToken));
        }

        return ExecuteL3OperationAsync(operation, cancellationToken);
    }

    private async ValueTask ExecuteL3OperationAsync(Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        var waitSucceeded = false;
        try
        {
            await _l3Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            waitSucceeded = true;
            await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("L3 operation was cancelled due to shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in L3 storage operation");
        }
        finally
        {
            if (waitSucceeded)
            {
                _l3Semaphore.Release();
            }
        }
    }

    private async ValueTask PromoteFromL3Async<T>(string key, T value, CancellationToken cancellationToken)
    {
        try
        {
            // Calculate appropriate expiration times for promotion
            var l1Expiration = CalculateL1Expiration(_options.L3DefaultExpiration);
            var l2Expiration = _options.L2DefaultExpiration;

            var promotionTasks = new List<ValueTask>
            {
                _l1Storage.SetAsync(key, value, l1Expiration, cancellationToken)
            };

            // Also promote to L2 if available
            if (_l2Storage != null && _options.L2Enabled)
            {
                promotionTasks.Add(WriteToL2Async(key, value, l2Expiration, Enumerable.Empty<string>(), cancellationToken));
            }

            await AwaitAllAsync(promotionTasks).ConfigureAwait(false);
            _logger.LogDebug("Promoted key {Key} from L3 to higher layers", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error promoting key {Key} from L3", key);
        }
    }

    private static async ValueTask AwaitAllAsync(List<ValueTask> tasks)
    {
        if (tasks.Count == 0) return;
        if (tasks.Count == 1)
        {
            await tasks[0].ConfigureAwait(false);
            return;
        }

        // Convert ValueTasks to Tasks for parallel execution
        var regularTasks = new Task[tasks.Count];
        for (int i = 0; i < tasks.Count; i++)
        {
            regularTasks[i] = tasks[i].AsTask();
        }

        await Task.WhenAll(regularTasks).ConfigureAwait(false);
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
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing asynchronous cache write operation");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when disposing
        }
    }

    private bool TryScheduleAsyncWrite(Func<CancellationToken, ValueTask> work)
    {
        if (_asyncWriteChannel == null || _asyncWriteCts == null)
        {
            return false;
        }

        if (_asyncWriteChannel.Writer.TryWrite(work))
        {
            return true;
        }

        return false;
    }

    private bool TryScheduleAwaitableAsyncWrite(Func<CancellationToken, ValueTask> work, out Task completionTask)
    {
        if (_asyncWriteChannel == null || _asyncWriteCts == null)
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
            completionTask = tcs.Task;
            return true;
        }

        completionTask = Task.CompletedTask;
        return false;
    }

    private static async Task WaitForScheduledWriteAsync(Task scheduledTask, CancellationToken cancellationToken)
    {
        await scheduledTask.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                // Already disposed by a prior call.
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
                    await subscriptionTask.ConfigureAwait(false);
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
                await _backplane.UnsubscribeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unsubscribe from backplane");
            }
        }
    }
}
