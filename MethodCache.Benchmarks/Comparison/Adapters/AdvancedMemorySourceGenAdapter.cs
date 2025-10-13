using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.Infrastructure.Extensions;
using MethodCache.Providers.Memory.Extensions;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace MethodCache.Benchmarks.Comparison.Adapters;

/// <summary>
/// MethodCache adapter using AdvancedMemoryStorage with direct API (no source generation needed for adapter comparison)
/// This compares MethodCache's AdvancedMemory storage implementation directly with other frameworks
/// </summary>
public class AdvancedMemorySourceGenAdapter : ICacheAdapter
{
    private readonly IMemoryStorage _storage;
    private readonly IServiceProvider _serviceProvider;
    private readonly CacheStatistics _stats = new();

    public string Name => "MethodCache (AdvancedMemory)";

    public AdvancedMemorySourceGenAdapter()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCache(config =>
        {
            config.DefaultPolicy(builder => builder.WithDuration(TimeSpan.FromMinutes(10)));
        }, typeof(AdvancedMemorySourceGenAdapter).Assembly);
        services.AddAdvancedMemoryStorage(); // Use AdvancedMemory provider

        _serviceProvider = services.BuildServiceProvider();
        _storage = _serviceProvider.GetRequiredService<IMemoryStorage>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet<TValue>(string key, out TValue? value)
    {
        value = _storage.Get<TValue>(key);
        var found = value != null && !EqualityComparer<TValue>.Default.Equals(value, default);

        if (found)
            _stats.Hits++;
        else
            _stats.Misses++;

        return found;
    }

    public async Task<TValue> GetOrSetAsync<TValue>(string key, Func<Task<TValue>> factory, TimeSpan duration)
    {
        var cached = await _storage.GetAsync<TValue>(key);

        if (cached != null && !EqualityComparer<TValue>.Default.Equals(cached, default))
        {
            _stats.Hits++;
            return cached;
        }

        // Cache miss - execute factory
        _stats.Misses++;
        _stats.FactoryCalls++;
        var sw = Stopwatch.StartNew();
        var result = await factory();
        sw.Stop();
        _stats.TotalFactoryDuration += sw.Elapsed;

        await _storage.SetAsync(key, result, duration);

        return result;
    }

    public void Set<TValue>(string key, TValue value, TimeSpan duration)
    {
        _storage.Set(key, value, duration);
    }

    public void Remove(string key)
    {
        _storage.Remove(key);
    }

    public void Clear()
    {
        _storage.Clear();
    }

    public CacheStatistics GetStatistics() => _stats;

    public void Dispose()
    {
        (_serviceProvider as IDisposable)?.Dispose();
    }
}
