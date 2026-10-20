using Microsoft.Extensions.Logging.Abstractions;
using MethodCache.HttpCaching.Internal;

namespace MethodCache.HttpCaching.Tests;

internal static class HttpCacheTestFactory
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

    internal static TestHttpStore CreateInMemoryStorage(long maximumBytes = 100 * 1024 * 1024, TimeProvider? timeProvider = null)
        => new(timeProvider, maximumBytes: maximumBytes);

    /// <summary>A response served fresh from the cache, by its Cache-Status member.</summary>
    internal const string FreshHit = @"^MethodCache; hit; ttl=\d+$";

    /// <summary>A stale response served while it revalidates in the background.</summary>
    internal const string StaleWhileRevalidateHit = @"^MethodCache; hit; ttl=-\d+; detail=stale-while-revalidate$";

    /// <summary>A stale response served because the origin failed without answering.</summary>
    internal const string StaleIfOriginFailed = @"^MethodCache; fwd=stale; ttl=-\d+; detail=stale-if-error$";

    /// <summary>This cache's member of the response's Cache-Status header (the last one), or <c>null</c> when it has none.</summary>
    internal static string? CacheStatus(HttpResponseMessage response)
        => response.Headers.TryGetValues("Cache-Status", out var values)
            ? values.SelectMany(value => value.Split(',')).Select(member => member.Trim())
                .LastOrDefault(member => member.StartsWith("MethodCache;", StringComparison.Ordinal))
            : null;

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
