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
        => CreateHandler(options, innerHandler, new HttpCacheClientState(clientName), storage, timeProvider);

    /// <summary>Creates a handler around <paramref name="state"/>, so a test can see which revalidations are in flight.</summary>
    internal static DelegatingHandler CreateHandler(
        HttpCacheOptions options,
        HttpMessageHandler innerHandler,
        HttpCacheClientState state,
        IHttpCacheStorage? storage = null,
        TimeProvider? timeProvider = null)
    {
        options.AllowUnverifiedTransport = true; // These test transports have no automatic cookie jar.
        storage ??= CreateInMemoryStorage(timeProvider: timeProvider);

        return new HttpCacheHandler(
            state,
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
        => CreateClient(options, new HttpCacheClientState("test"), storage, timeProvider);

    internal static (HttpClient Client, TestHttpMessageHandler InnerHandler) CreateClient(
        HttpCacheOptions options,
        HttpCacheClientState state,
        IHttpCacheStorage? storage = null,
        TimeProvider? timeProvider = null)
    {
        var inner = new TestHttpMessageHandler();
        var handler = CreateHandler(options, inner, state, storage, timeProvider);
        return (new HttpClient(handler), inner);
    }

    public static IHttpCacheStorage CreateInMemoryStorage(HttpCacheStorageOptions? storageOptions = null, TimeProvider? timeProvider = null)
        => new HttpCacheStore(
            Microsoft.Extensions.Options.Options.Create(storageOptions ?? new HttpCacheStorageOptions()),
            NullLogger<HttpCacheStore>.Instance,
            timeProvider);

    /// <summary>Waits for background work, such as a revalidation, without guessing how long it takes.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTimeProvider : Microsoft.Extensions.Time.Testing.FakeTimeProvider
{
    public ManualTimeProvider() : base(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)) { }
}
