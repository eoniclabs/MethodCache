using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Storage;
using MethodCache.Core.Storage.Abstractions;
using MethodCache.Providers.Memory.Configuration;

namespace MethodCache.Providers.Memory.Infrastructure;

public class AdvancedMemoryStorage : IMemoryStorage, IAsyncDisposable, IDisposable
{
    private readonly AdvancedMemoryStorageProvider _provider;
    private readonly ILogger<AdvancedMemoryStorage> _logger;
    private readonly bool _ownsProvider;
    private bool _disposed;

    public AdvancedMemoryStorage(
        AdvancedMemoryStorageProvider provider,
        ILogger<AdvancedMemoryStorage> logger)
    {
        _provider = provider;
        _logger = logger;
        _ownsProvider = false;
    }

    public AdvancedMemoryStorage(
        IOptions<AdvancedMemoryOptions> options,
        ILogger<AdvancedMemoryStorage> logger,
        ILogger<AdvancedMemoryStorageProvider> providerLogger)
    {
        _logger = logger;
        _provider = new AdvancedMemoryStorageProvider(options, providerLogger);
        _ownsProvider = true;
    }

    public T? Get<T>(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        return _provider.Get<T>(key);
    }

    public void Set<T>(string key, T value, TimeSpan expiration)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.Set(key, value, expiration, Array.Empty<string>());
    }

    public void Set<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.Set(key, value, expiration, tags);
    }

    public void Remove(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.Remove(key);
    }

    public void RemoveByTag(string tag)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        _provider.RemoveByTag(tag);
    }

    public bool Exists(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        return _provider.Exists(key);
    }

    public MemoryStorageStats GetStats()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        var storageStats = _provider.GetStats();

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

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        return await _provider.GetAsync<T>(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.SetAsync(key, value, expiration, Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.SetAsync(key, value, expiration, tags, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdvancedMemoryStorage));
        await _provider.RemoveByTagAsync(tag, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_ownsProvider)
        {
            _provider.Dispose();
        }
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
