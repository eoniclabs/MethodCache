using MethodCache.Core.Storage.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MethodCache.Core.Storage.Layers.Memory;

/// <summary>
/// Factory for creating memory L1 providers.
/// </summary>
public static class Memory
{
    /// <summary>
    /// Creates a default memory L1 provider.
    /// </summary>
    /// <returns>A configured L1 memory provider.</returns>
    public static IL1Provider Default() => new MemoryL1Provider();

    /// <summary>
    /// Creates a memory L1 provider with custom configuration.
    /// </summary>
    /// <param name="configure">Configuration action.</param>
    /// <returns>A configured L1 memory provider.</returns>
    public static IL1Provider Configure(Action<MemoryOptions> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        return new MemoryL1Provider().Configure(configure);
    }

    /// <summary>
    /// Creates a memory L1 provider with a specific maximum size.
    /// </summary>
    /// <param name="maxSize">Maximum number of items in memory.</param>
    /// <returns>A configured L1 memory provider.</returns>
    public static IL1Provider WithMaxSize(int maxSize)
    {
        if (maxSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxSize), "Max size must be greater than zero.");
        return new MemoryL1Provider().WithMaxSize(maxSize);
    }
}

/// <summary>
/// Memory L1 provider implementation.
/// </summary>
public class MemoryL1Provider : IL1Provider
{
    internal MemoryOptions Options { get; set; } = new();

    public string Name => "Memory";

    /// <summary>
    /// Configures the memory provider options.
    /// </summary>
    /// <param name="configure">Configuration action.</param>
    /// <returns>This provider for chaining.</returns>
    public MemoryL1Provider Configure(Action<MemoryOptions> configure)
    {
        if (configure == null) throw new ArgumentNullException(nameof(configure));
        configure(Options);
        return this;
    }

    /// <summary>
    /// Sets the maximum number of items in memory.
    /// </summary>
    /// <param name="maxSize">Maximum size.</param>
    /// <returns>This provider for chaining.</returns>
    public MemoryL1Provider WithMaxSize(int maxSize)
    {
        if (maxSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxSize));
        Options.MaxSize = maxSize;
        return this;
    }

    /// <summary>
    /// Sets the default expiration time.
    /// </summary>
    /// <param name="expiration">Default expiration time.</param>
    /// <returns>This provider for chaining.</returns>
    public MemoryL1Provider WithDefaultExpiration(TimeSpan expiration)
    {
        if (expiration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(expiration));
        Options.DefaultExpiration = expiration;
        return this;
    }

    public void Register(IServiceCollection services)
    {
        // Configure memory options
        services.Configure<MemoryOptions>(opt =>
        {
            opt.MaxSize = Options.MaxSize;
            opt.DefaultExpiration = Options.DefaultExpiration;
        });

        // Register memory storage implementation
        services.TryAddSingleton<IMemoryStorage, MemoryStorage>();
    }

    public void ValidateConfiguration()
    {
        if (Options.MaxSize <= 0)
            throw new InvalidOperationException("Memory max size must be greater than zero.");

        if (Options.DefaultExpiration <= TimeSpan.Zero)
            throw new InvalidOperationException("Memory default expiration must be greater than zero.");
    }
}

/// <summary>
/// Configuration options for memory storage.
/// </summary>
public class MemoryOptions
{
    /// <summary>
    /// Maximum number of items to store in memory.
    /// </summary>
    public int MaxSize { get; set; } = 10000;

    /// <summary>
    /// Default expiration time for memory cache entries.
    /// </summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);
}