using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Internal;

namespace MethodCache.HttpCaching.Tests;

public static class HttpCacheTestFactory
{
    public static DelegatingHandler CreateHandler(
        HttpCacheOptions options,
        HttpMessageHandler innerHandler,
        IHttpCacheStorage? storage = null,
        TimeProvider? timeProvider = null,
        string clientName = "test")
    {
        storage ??= CreateInMemoryStorage();

        return new HttpCacheHandler(
            new HttpCacheClientState(clientName),
            storage,
            new TestOptionsMonitor<HttpCacheOptions>(options),
            timeProvider ?? TimeProvider.System,
            NullLogger<HttpCacheHandler>.Instance)
        {
            InnerHandler = innerHandler
        };
    }

    public static (HttpClient Client, TestHttpMessageHandler InnerHandler) CreateClient(
        HttpCacheOptions options,
        IHttpCacheStorage? storage = null,
        TimeProvider? timeProvider = null)
    {
        var inner = new TestHttpMessageHandler();
        var handler = CreateHandler(options, inner, storage, timeProvider);
        return (new HttpClient(handler), inner);
    }

    public static IHttpCacheStorage CreateInMemoryStorage(HttpCacheStorageOptions? storageOptions = null)
        => new InMemoryHttpCacheStorage(
            Microsoft.Extensions.Options.Options.Create(storageOptions ?? new HttpCacheStorageOptions()),
            NullLogger<InMemoryHttpCacheStorage>.Instance);
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
