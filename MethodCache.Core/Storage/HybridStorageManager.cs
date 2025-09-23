using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Configuration;

namespace MethodCache.Core.Storage;

/// <summary>
/// Manages hybrid storage with L1 (memory), L2 (distributed), and optional L3 (persistent) layers.
/// </summary>
public class HybridStorageManager : IStorageProvider
{
    private readonly IMemoryStorage _l1Storage;
    private readonly IStorageProvider? _l2Storage;
    private readonly IPersistentStorageProvider? _l3Storage;
    private readonly IBackplane? _backplane;
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

    public string Name => $"Hybrid(L1+{(_l2Storage != null ? "L2+" : "")}{(_l3Storage != null ? "L3" : "Memory-Only")})";

    public HybridStorageManager(
        IMemoryStorage l1Storage,
        IOptions<StorageOptions> options,
        ILogger<HybridStorageManager> logger,
        IStorageProvider? l2Storage = null,
        IPersistentStorageProvider? l3Storage = null,
        IBackplane? backplane = null)
    {
        _l1Storage = l1Storage;
        _l2Storage = l2Storage;
        _l3Storage = l3Storage;
        _backplane = backplane;
        _options = options.Value;
        _logger = logger;
        _l2Semaphore = new SemaphoreSlim(_options.MaxConcurrentL2Operations, _options.MaxConcurrentL2Operations);
        _l3Semaphore = new SemaphoreSlim(_options.MaxConcurrentL3Operations, _options.MaxConcurrentL3Operations);

        if (_backplane != null)
        {
            // Subscribe to backplane messages for distributed invalidation
            _ = Task.Run(async () =>
            {
                try
                {
                    await _backplane.SubscribeAsync(OnBackplaneMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to subscribe to backplane messages");
                }
            });
        }
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        // Try L1 first
        var result = await _l1Storage.GetAsync<T>(key, cancellationToken);
        if (result != null)
        {
            Interlocked.Increment(ref _l1Hits);
            _logger.LogDebug("L1 cache hit for key {Key}", key);
            return result;
        }

        Interlocked.Increment(ref _l1Misses);

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
                    _logger.LogDebug("L2 cache hit for key {Key}", key);

                    // Warm L1 cache with shorter expiration
                    var l1Expiration = CalculateL1Expiration(_options.L2DefaultExpiration);
                    await _l1Storage.SetAsync(key, result, l1Expiration, cancellationToken);

                    return result;
                }

                Interlocked.Increment(ref _l2Misses);
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
                    _logger.LogDebug("L3 cache hit for key {Key}", key);

                    // Promote to higher layers if enabled
                    if (_options.EnableL3Promotion)
                    {
                        await PromoteFromL3Async(key, result, cancellationToken);
                    }

                    return result;
                }

                Interlocked.Increment(ref _l3Misses);
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

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        await SetAsync(key, value, expiration, Enumerable.Empty<string>(), cancellationToken);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var l1Expiration = CalculateL1Expiration(expiration);
        var tasks = new List<Task>();

        // Always set in L1
        tasks.Add(_l1Storage.SetAsync(key, value, l1Expiration, tags, cancellationToken));

        // Set in L2 if enabled
        if (_l2Storage != null && _options.L2Enabled)
        {
            if (_options.EnableAsyncL2Writes)
            {
                // Fire and forget for async writes
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _l2Semaphore.WaitAsync(cancellationToken);
                        await _l2Storage.SetAsync(key, value, expiration, tags, cancellationToken);
                        _logger.LogDebug("Async L2 write completed for key {Key}", key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in async L2 write for key {Key}", key);
                    }
                    finally
                    {
                        _l2Semaphore.Release();
                    }
                }, cancellationToken);
            }
            else
            {
                // Synchronous L2 write
                tasks.Add(WriteToL2Async(key, value, expiration, tags, cancellationToken));
            }
        }

        // Set in L3 if enabled
        if (_l3Storage != null && _options.L3Enabled)
        {
            var l3Expiration = CalculateL3Expiration(expiration);

            if (_options.EnableAsyncL3Writes)
            {
                // Fire and forget for async writes
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _l3Semaphore.WaitAsync(cancellationToken);
                        await _l3Storage.SetAsync(key, value, l3Expiration, tags, cancellationToken);
                        _logger.LogDebug("Async L3 write completed for key {Key}", key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error in async L3 write for key {Key}", key);
                    }
                    finally
                    {
                        _l3Semaphore.Release();
                    }
                }, cancellationToken);
            }
            else
            {
                // Synchronous L3 write
                tasks.Add(WriteToL3Async(key, value, l3Expiration, tags, cancellationToken));
            }
        }

        await Task.WhenAll(tasks);
        _logger.LogDebug("Set key {Key} in hybrid storage with expiration {Expiration}", key, expiration);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task>
        {
            _l1Storage.RemoveAsync(key, cancellationToken)
        };

        if (_l2Storage != null && _options.L2Enabled)
        {
            tasks.Add(WriteToL2Async(() => _l2Storage.RemoveAsync(key, cancellationToken)));
        }

        if (_l3Storage != null && _options.L3Enabled)
        {
            tasks.Add(WriteToL3Async(() => _l3Storage.RemoveAsync(key, cancellationToken)));
        }

        await Task.WhenAll(tasks);

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

    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        var tasks = new List<Task>
        {
            _l1Storage.RemoveByTagAsync(tag, cancellationToken)
        };

        if (_l2Storage != null && _options.L2Enabled)
        {
            tasks.Add(WriteToL2Async(() => _l2Storage.RemoveByTagAsync(tag, cancellationToken)));
        }

        if (_l3Storage != null && _options.L3Enabled)
        {
            tasks.Add(WriteToL3Async(() => _l3Storage.RemoveByTagAsync(tag, cancellationToken)));
        }

        await Task.WhenAll(tasks);

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

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        // Check L1 first
        if (_l1Storage.Exists(key))
        {
            return true;
        }

        // Check L2 if enabled
        if (_l2Storage != null && _options.L2Enabled)
        {
            try
            {
                await _l2Semaphore.WaitAsync(cancellationToken);
                var l2Exists = await _l2Storage.ExistsAsync(key, cancellationToken);
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
                await _l3Semaphore.WaitAsync(cancellationToken);
                return await _l3Storage.ExistsAsync(key, cancellationToken);
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

    public async Task<HealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var l1Healthy = true; // Memory storage is generally always healthy
        var l2Healthy = true;
        var l3Healthy = true;

        if (_l2Storage != null && _options.L2Enabled)
        {
            try
            {
                l2Healthy = await _l2Storage.GetHealthAsync(cancellationToken) == HealthStatus.Healthy;
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
                l3Healthy = await _l3Storage.GetHealthAsync(cancellationToken) == HealthStatus.Healthy;
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

    public async Task<StorageStats?> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var l1Stats = _l1Storage.GetStats();
        var l2Stats = _l2Storage != null ? await _l2Storage.GetStatsAsync(cancellationToken) : null;
        var l3Stats = _l3Storage != null ? await _l3Storage.GetStatsAsync(cancellationToken) : null;

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
        var l1Expiration = TimeSpan.FromTicks(Math.Min(
            originalExpiration.Ticks,
            _options.L1MaxExpiration.Ticks));

        return TimeSpan.FromTicks(Math.Max(
            l1Expiration.Ticks,
            _options.L1DefaultExpiration.Ticks));
    }

    private async Task WriteToL2Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        if (_l2Storage == null)
            return;

        try
        {
            await _l2Semaphore.WaitAsync(cancellationToken);
            await _l2Storage.SetAsync(key, value, expiration, tags, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to L2 storage for key {Key}", key);
        }
        finally
        {
            _l2Semaphore.Release();
        }
    }

    private async Task WriteToL2Async(Func<Task> operation)
    {
        try
        {
            await _l2Semaphore.WaitAsync();
            await operation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in L2 storage operation");
        }
        finally
        {
            _l2Semaphore.Release();
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

    private async Task WriteToL3Async<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken)
    {
        if (_l3Storage == null)
            return;

        try
        {
            await _l3Semaphore.WaitAsync(cancellationToken);
            await _l3Storage.SetAsync(key, value, expiration, tags, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to L3 storage for key {Key}", key);
        }
        finally
        {
            _l3Semaphore.Release();
        }
    }

    private async Task WriteToL3Async(Func<Task> operation)
    {
        try
        {
            await _l3Semaphore.WaitAsync();
            await operation();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in L3 storage operation");
        }
        finally
        {
            _l3Semaphore.Release();
        }
    }

    private async Task PromoteFromL3Async<T>(string key, T value, CancellationToken cancellationToken)
    {
        try
        {
            // Calculate appropriate expiration times for promotion
            var l1Expiration = CalculateL1Expiration(_options.L3DefaultExpiration);
            var l2Expiration = _options.L2DefaultExpiration;

            var promotionTasks = new List<Task>
            {
                _l1Storage.SetAsync(key, value, l1Expiration, cancellationToken)
            };

            // Also promote to L2 if available
            if (_l2Storage != null && _options.L2Enabled)
            {
                promotionTasks.Add(WriteToL2Async(key, value, l2Expiration, Enumerable.Empty<string>(), cancellationToken));
            }

            await Task.WhenAll(promotionTasks);
            _logger.LogDebug("Promoted key {Key} from L3 to higher layers", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error promoting key {Key} from L3", key);
        }
    }
}