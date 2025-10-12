using System;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core.Configuration;
using McmMemoryCacheOptions = Microsoft.Extensions.Caching.Memory.MemoryCacheOptions;

namespace MethodCache.Core.Infrastructure.Configuration;

internal sealed class StorageMemoryCacheConfigurator : IConfigureOptions<McmMemoryCacheOptions>
{
    private readonly IOptions<StorageOptions> _storageOptions;
    private readonly ILogger<StorageMemoryCacheConfigurator> _logger;

    public StorageMemoryCacheConfigurator(
        IOptions<StorageOptions> storageOptions,
        ILogger<StorageMemoryCacheConfigurator> logger)
    {
        _storageOptions = storageOptions;
        _logger = logger;
    }

    public void Configure(McmMemoryCacheOptions options)
    {
        var storage = _storageOptions.Value;

        if (options.SizeLimit == null)
        {
            long? sizeLimit = null;

            if (storage.L1MaxMemoryBytes > 0)
            {
                sizeLimit = storage.L1MaxMemoryBytes;
            }
            else if (storage.L1MaxItems > 0)
            {
                sizeLimit = storage.L1MaxItems;
            }

            if (sizeLimit.HasValue)
            {
                options.SizeLimit = sizeLimit;
                _logger.LogDebug("Configured memory cache size limit to {SizeLimit}", sizeLimit);
            }
        }

        if (storage.L1EvictionPolicy == L1EvictionPolicy.TTL)
        {
            // TTL policies benefit from more aggressive scans to remove expired entries promptly.
            options.ExpirationScanFrequency = TimeSpan.FromMinutes(1);
        }
    }
}
