using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Providers.Memory.Configuration;

namespace MethodCache.Providers.Memory.Infrastructure;

public class AdvancedMemoryStorage : IMemoryStorage, IAsyncDisposable, IDisposable
{
    private readonly AdvancedMemoryStorageProvider _provider;
    private readonly ILogger<AdvancedMemoryStorage> _logger;
    private bool _disposed;

    public AdvancedMemoryStorage(
        IOptions<AdvancedMemoryOptions> options,
        ILogger<AdvancedMemoryStorage> logger,
        ILogger<AdvancedMemoryStorageProvider> providerLogger)
    {
        _logger = logger;
        _provider = new AdvancedMemoryStorageProvider(options, providerLogger);
    }

    public T? Get<T>(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        return _provider.GetAsync<T>(key).GetAwaiter().GetResult();
    }

    public void Set<T>(string key, T value, TimeSpan expiration)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.SetAsync(key, value, expiration, Array.Empty<string>()).GetAwaiter().GetResult();
    }

    public void Set<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.SetAsync(key, value, expiration, tags).GetAwaiter().GetResult();
    }

    public void Remove(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.RemoveAsync(key).GetAwaiter().GetResult();
    }

    public void RemoveByTag(string tag)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.RemoveByTagAsync(tag).GetAwaiter().GetResult();
    }

    public bool Exists(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        return _provider.ExistsAsync(key).GetAwaiter().GetResult();
    }

    public MemoryStorageStats GetStats()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        var storageStats = _provider.GetStatsAsync().GetAwaiter().GetResult();

        if (storageStats == null)
        {
            return new MemoryStorageStats();
        }

        return new MemoryStorageStats
        {
            EntryCount = Convert.ToInt64(storageStats.AdditionalStats.TryGetValue("EntryCount", out var entryCount) ? entryCount : 0),
            Hits = Convert.ToInt64(storageStats.AdditionalStats.TryGetValue("Hits", out var hits) ? hits : 0),
            Misses = Convert.ToInt64(storageStats.AdditionalStats.TryGetValue("Misses", out var misses) ? misses : 0),
            Evictions = Convert.ToInt64(storageStats.AdditionalStats.TryGetValue("Evictions", out var evictions) ? evictions : 0),
            EstimatedMemoryUsage = Convert.ToInt64(storageStats.AdditionalStats.TryGetValue("EstimatedMemoryUsage", out var memory) ? memory : 0),
            TagMappingCount = Convert.ToInt32(storageStats.AdditionalStats.TryGetValue("TagMappingCount", out var tagMappings) ? tagMappings : 0)
        };
    }

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.Clear();
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        return await _provider.GetAsync<T>(key, cancellationToken);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.SetAsync(key, value, expiration, Array.Empty<string>(), cancellationToken);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.SetAsync(key, value, expiration, tags, cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.RemoveAsync(key, cancellationToken);
    }

    public async Task RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.RemoveByTagAsync(tag, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _provider.Dispose();
        _disposed = true;
        _logger.LogInformation("AdvancedMemoryStorage disposed");
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;

        Dispose();
        return ValueTask.CompletedTask;
    }
}