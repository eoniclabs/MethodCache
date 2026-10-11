using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Tests;
using Xunit;

namespace MethodCache.HttpCaching.IntegrationTests;

public class HttpCacheIntegrationTests
{
    [Fact]
    public void AddHttpCaching_RegistersServicesCorrectly()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<TestApiClient>()
            .AddHttpCaching();

        var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<TestApiClient>();
        Assert.NotNull(client);

        var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(nameof(TestApiClient));
        Assert.NotNull(httpClient);

        var storage = provider.GetRequiredService<IHttpCacheStorage>();
        Assert.IsType<MethodCache.HttpCaching.Internal.HttpCacheStore>(storage);
    }

    [Fact]
    public void AddHttpCaching_WithConfiguration_AppliesOptions()
    {
        var services = new ServiceCollection();
        services.Configure<HttpCacheStorageOptions>(storage => storage.MaxCacheSize = 50 * 1024 * 1024);
        services.AddHttpClient<TestApiClient>()
            .AddHttpCaching(options =>
            {
                options.EnableStaleWhileRevalidate = true;
                options.EnableStaleIfError = true;
                options.DefaultMaxAge = TimeSpan.FromMinutes(10);
                options.AddDiagnosticHeaders = true;
            });

        var provider = services.BuildServiceProvider();

        var cacheOptions = provider.GetRequiredService<IOptionsMonitor<HttpCacheOptions>>().Get(nameof(TestApiClient));
        Assert.True(cacheOptions.EnableStaleWhileRevalidate);
        Assert.True(cacheOptions.EnableStaleIfError);
        Assert.Equal(TimeSpan.FromMinutes(10), cacheOptions.DefaultMaxAge);
        Assert.True(cacheOptions.AddDiagnosticHeaders);

        var storageOptions = provider.GetRequiredService<IOptions<HttpCacheStorageOptions>>().Value;
        Assert.Equal(50 * 1024 * 1024, storageOptions.MaxCacheSize);
    }

    [Fact]
    public void AddHttpCaching_CanUseCustomStorage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpCacheStorage, FakeHttpCacheStorage>();

        services.AddHttpClient<TestApiClient>()
            .AddHttpCaching();

        var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHttpCacheStorage>();
        Assert.IsType<FakeHttpCacheStorage>(storage);
    }

    private sealed class FakeHttpCacheStorage : IHttpCacheStorage
    {
        public ValueTask<string> GetGenerationAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult(string.Empty);
        public ValueTask InvalidateAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult<HttpCacheEntry?>(null);
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetAsync(string key, HttpCacheEntry entry, TimeSpan timeToLive, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    public class TestApiClient
    {
        public TestApiClient(HttpClient httpClient)
        {
            HttpClient = httpClient;
        }

        public HttpClient HttpClient { get; }
    }
}
