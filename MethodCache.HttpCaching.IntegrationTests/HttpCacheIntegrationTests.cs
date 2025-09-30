using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using MethodCache.HttpCaching.Options;
using MethodCache.HttpCaching.Storage;
using MethodCache.HttpCaching.Tests;
using MethodCache.HttpCaching.Validation;
using MethodCache.HttpCaching.Extensions;
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
        Assert.IsType<InMemoryHttpCacheStorage>(storage);
    }

    [Fact]
    public void AddHttpCaching_WithBuilderConfiguration_AppliesOptions()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<TestApiClient>()
            .AddHttpCaching(builder => builder
                .Configure(options =>
                {
                    options.Behavior.EnableStaleWhileRevalidate = true;
                    options.Behavior.EnableStaleIfError = true;
                    options.Freshness.DefaultMaxAge = TimeSpan.FromMinutes(10);
                    options.Diagnostics.AddDiagnosticHeaders = true;
                })
                .ConfigureStorage(storage => storage.MaxCacheSize = 50 * 1024 * 1024));

        var provider = services.BuildServiceProvider();

        var cacheOptions = provider.GetRequiredService<IOptions<HttpCacheOptions>>().Value;
        Assert.True(cacheOptions.Behavior.EnableStaleWhileRevalidate);
        Assert.True(cacheOptions.Behavior.EnableStaleIfError);
        Assert.Equal(TimeSpan.FromMinutes(10), cacheOptions.Freshness.DefaultMaxAge);
        Assert.True(cacheOptions.Diagnostics.AddDiagnosticHeaders);

        var storageOptions = provider.GetRequiredService<IOptions<HttpCacheStorageOptions>>().Value;
        Assert.Equal(50 * 1024 * 1024, storageOptions.MaxCacheSize);
    }

    [Fact]
    public void AddHttpCaching_CanUseCustomStorage()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpCacheStorage, FakeHttpCacheStorage>();

        services.AddHttpClient<TestApiClient>()
            .AddHttpCaching(builder => builder.ConfigureStorage(_ => { }));

        var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<IHttpCacheStorage>();
        Assert.IsType<FakeHttpCacheStorage>(storage);
    }

    private sealed class FakeHttpCacheStorage : IHttpCacheStorage
    {
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<HttpCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default) => ValueTask.FromResult<HttpCacheEntry?>(null);
        public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetAsync(string key, HttpCacheEntry entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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
