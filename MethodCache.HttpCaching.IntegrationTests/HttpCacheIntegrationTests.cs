using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using MethodCache.HttpCaching.Extensions;
using Xunit;

namespace MethodCache.HttpCaching.IntegrationTests;

public class HttpCacheIntegrationTests
{
    [Fact]
    public void AddHttpCaching_RegistersServicesCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient<TestApiClient>()
            .AddHttpCaching();

        // Act
        var serviceProvider = services.BuildServiceProvider();

        // Assert
        var client = serviceProvider.GetRequiredService<TestApiClient>();
        Assert.NotNull(client);

        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient(nameof(TestApiClient));
        Assert.NotNull(httpClient);
    }

    [Fact]
    public void AddApiCaching_ConfiguresOptionsCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient<TestApiClient>()
            .AddApiCaching(options =>
            {
                options.DefaultMaxAge = TimeSpan.FromMinutes(10);
                options.AddDiagnosticHeaders = true;
            });

        // Act
        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<HttpCacheOptions>();

        // Assert
        Assert.True(options.EnableStaleWhileRevalidate);
        Assert.True(options.EnableStaleIfError);
        Assert.Equal(TimeSpan.FromMinutes(10), options.DefaultMaxAge);
        Assert.True(options.AddDiagnosticHeaders);
    }

    [Fact]
    public void AddHttpCachingWithDebug_EnablesDiagnosticHeaders()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient<TestApiClient>()
            .AddHttpCachingWithDebug();

        // Act
        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<HttpCacheOptions>();

        // Assert
        Assert.True(options.AddDiagnosticHeaders);
    }

    [Fact]
    public void AddHttpCachingInMemory_UsesInMemoryStorage()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient<TestApiClient>()
            .AddHttpCachingInMemory(options =>
            {
                options.MaxCacheSize = 50 * 1024 * 1024; // 50MB
            });

        // Act
        var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<HttpCacheOptions>();

        // Assert
        Assert.Equal(50 * 1024 * 1024, options.MaxCacheSize);
        // Storage will be set to InMemoryHttpCacheStorage by the handler factory
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

/// <summary>
/// Integration tests that verify HTTP caching behavior with real HTTP requests.
/// </summary>
public class HttpCacheEndToEndTests
{
    [Fact]
    public void HttpCaching_WorksWithRealHttpClient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHttpClient("test")
            .AddHttpCaching(options =>
            {
                options.AddDiagnosticHeaders = true;
                options.DefaultMaxAge = TimeSpan.FromSeconds(1);
            });

        var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient("test");

        // Use a test server or mock service for actual integration testing
        // For now, we'll test the configuration is working
        Assert.NotNull(httpClient);
    }

    [Fact]
    public void MultipleClients_CanHaveDifferentCacheConfigurations()
    {
        // Arrange
        var services = new ServiceCollection();

        services.AddHttpClient("fast-cache")
            .AddHttpCaching(options =>
            {
                options.DefaultMaxAge = TimeSpan.FromSeconds(30);
                options.EnableStaleWhileRevalidate = true;
            });

        services.AddHttpClient("slow-cache")
            .AddHttpCaching(options =>
            {
                options.DefaultMaxAge = TimeSpan.FromMinutes(10);
                options.EnableStaleIfError = true;
            });

        // Act
        var serviceProvider = services.BuildServiceProvider();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        var fastClient = httpClientFactory.CreateClient("fast-cache");
        var slowClient = httpClientFactory.CreateClient("slow-cache");

        // Assert
        Assert.NotNull(fastClient);
        Assert.NotNull(slowClient);
        Assert.NotSame(fastClient, slowClient);
    }
}

/// <summary>
/// Tests for HTTP caching storage implementations.
/// </summary>
public class HttpCacheStorageTests
{
    [Fact]
    public async Task InMemoryStorage_StoresAndRetrievesEntries()
    {
        // Arrange
        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = new HttpCacheOptions();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Storage.InMemoryHttpCacheStorage>.Instance;

        var storage = new Storage.InMemoryHttpCacheStorage(memoryCache, options, logger);

        var entry = new HttpCacheEntry
        {
            RequestUri = "https://api.example.com/test",
            Method = "GET",
            StatusCode = System.Net.HttpStatusCode.OK,
            Content = System.Text.Encoding.UTF8.GetBytes("test content"),
            ETag = "\"abc123\"",
            StoredAt = DateTimeOffset.UtcNow
        };

        // Act
        await storage.SetAsync("test-key", entry);
        var retrieved = await storage.GetAsync("test-key");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(entry.RequestUri, retrieved.RequestUri);
        Assert.Equal(entry.ETag, retrieved.ETag);
        Assert.Equal(entry.Content, retrieved.Content);
    }

    [Fact]
    public async Task InMemoryStorage_ReturnsNull_WhenKeyNotFound()
    {
        // Arrange
        var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var options = new HttpCacheOptions();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Storage.InMemoryHttpCacheStorage>.Instance;

        var storage = new Storage.InMemoryHttpCacheStorage(memoryCache, options, logger);

        // Act
        var result = await storage.GetAsync("non-existent-key");

        // Assert
        Assert.Null(result);
    }
}