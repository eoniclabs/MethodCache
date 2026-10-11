using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Validation;

namespace MethodCache.HttpCaching.Tests;

public static class HttpCacheTestFactory
{
    public static DelegatingHandler CreateHandler(
        HttpCacheOptions options,
        HttpMessageHandler innerHandler,
        HttpCacheStorageOptions? storageOptions = null,
        IHttpCacheStorage? storage = null,
        TimeProvider? timeProvider = null,
        string clientName = "test")
    {
        storage ??= new InMemoryHttpCacheStorage(
            Microsoft.Extensions.Options.Options.Create(storageOptions ?? new HttpCacheStorageOptions()),
            NullLogger<InMemoryHttpCacheStorage>.Instance);

        return new HttpCacheHandler(
            new HttpCacheClientState(clientName),
            storage,
            new TestOptionsMonitor<HttpCacheOptions>(options),
            new VaryHeaderCacheKeyGenerator(),
            timeProvider ?? TimeProvider.System,
            NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };
    }

    public static (HttpClient Client, TestHttpMessageHandler InnerHandler) CreateClient(
        HttpCacheOptions options,
        HttpCacheStorageOptions? storageOptions = null,
        IHttpCacheStorage? storage = null,
        TimeProvider? timeProvider = null)
    {
        var inner = new TestHttpMessageHandler();
        var handler = CreateHandler(options, inner, storageOptions, storage, timeProvider);
        return (new HttpClient(handler), inner);
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
