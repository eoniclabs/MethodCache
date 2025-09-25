using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;

namespace MethodCache.HttpCaching.Tests;

internal static class HttpCacheTestFactory
{
    public static HttpCacheHandler CreateHandler(
        HttpCacheOptions options,
        HttpMessageHandler innerHandler,
        HttpCacheStorageOptions? storageOptions = null,
        IMemoryCache? memoryCache = null)
    {
        storageOptions ??= new HttpCacheStorageOptions();
        memoryCache ??= new MemoryCache(new MemoryCacheOptions());

        var storage = new InMemoryHttpCacheStorage(
            memoryCache,
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(storageOptions),
            NullLogger<InMemoryHttpCacheStorage>.Instance);

        return new HttpCacheHandler(
            storage,
            new TestOptionsMonitor<HttpCacheOptions>(options),
            new VaryHeaderCacheKeyGenerator(),
            NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };
    }

    public static (HttpClient Client, TestHttpMessageHandler InnerHandler) CreateClient(
        HttpCacheOptions options,
        HttpCacheStorageOptions? storageOptions = null,
        IMemoryCache? memoryCache = null)
    {
        var inner = new TestHttpMessageHandler();
        var handler = CreateHandler(options, inner, storageOptions, memoryCache);
        return (new HttpClient(handler), inner);
    }
}

