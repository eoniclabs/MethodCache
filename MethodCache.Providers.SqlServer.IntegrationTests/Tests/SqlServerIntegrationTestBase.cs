using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.HybridCache.Extensions;
using MethodCache.Infrastructure.Abstractions;
using MethodCache.Infrastructure.Configuration;
using MethodCache.Infrastructure.Extensions;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Extensions;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;
using Testcontainers.MsSql;
using Xunit;
using DotNet.Testcontainers.Builders;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

public abstract class SqlServerIntegrationTestBase : IAsyncLifetime
{
    protected MsSqlContainer SqlServerContainer { get; private set; } = null!;
    protected IServiceProvider ServiceProvider { get; private set; } = null!;
    protected ICacheManager CacheManager { get; private set; } = null!;
    protected string SqlServerConnectionString { get; private set; } = null!;

    // Share a single SQL Server container across all test classes to reduce startup cost
    private static readonly SemaphoreSlim InitLock = new(1, 1);
    private static MsSqlContainer? SharedContainer;

    public async Task InitializeAsync()
    {
        await InitLock.WaitAsync();
        try
        {
            if (SharedContainer == null)
            {
                // If an external SQL Server is provided, do not create a container
                var external = Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_URL")
                               ?? Environment.GetEnvironmentVariable("SQLSERVER_URL");
                if (string.IsNullOrWhiteSpace(external))
                {
                    try
                    {
                        SharedContainer = new MsSqlBuilder()
                            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                            .WithPassword("YourStrong@Passw0rd")
                            .WithPortBinding(1433, true)
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("/opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P YourStrong@Passw0rd -Q \"SELECT 1\""))
                            .WithReuse(true)
                            .Build();

                        // Start with a reasonable timeout to avoid hanging the test run
                        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                        await SharedContainer.StartAsync(cts.Token);
                    }
                    catch (Exception ex)
                    {
                        // If Docker is unavailable, fail initialization with clear guidance
                        throw new InvalidOperationException($"Docker not available for SQL Server integration tests. {ex.Message}\nSet METHODCACHE_SQLSERVER_URL or SQLSERVER_URL to run against an external SQL Server, or start Docker.");
                    }
                }
                else
                {
                    // Using external SQL Server, so no container is created
                    SharedContainer = null;
                }
            }
        }
        finally
        {
            InitLock.Release();
        }

        // Use the shared container for this test class if present
        SqlServerContainer = SharedContainer!;
        SqlServerConnectionString = SharedContainer != null
            ? SharedContainer.GetConnectionString()
            : (Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_URL")
               ?? Environment.GetEnvironmentVariable("SQLSERVER_URL")
               ?? throw new InvalidOperationException("No SQL Server connection available."));

        var services = new ServiceCollection();
        // Reduce logging noise and overhead for CI speed
        services.AddLogging();

        // Use a modified SQL Server Infrastructure setup
        services.AddSqlServerInfrastructureForTests(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            // Unique prefix per test class instance to avoid cross-test collisions
            options.KeyPrefix = $"test:{Guid.NewGuid():N}:";
            options.Schema = $"test_{Guid.NewGuid():N}".Replace("-", "");
        });

        // Register infrastructure-based cache manager
        services.AddSingleton<ICacheManager>(provider =>
        {
            var storageProvider = provider.GetRequiredService<IStorageProvider>();
            var keyGenerator = provider.GetService<ICacheKeyGenerator>() ?? new DefaultCacheKeyGenerator();
            return new StorageProviderCacheManager(storageProvider, keyGenerator);
        });
        services.AddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();

        ServiceProvider = services.BuildServiceProvider();

        await StartHostedServicesAsync(ServiceProvider);

        // Initialize tables
        var tableManager = ServiceProvider.GetRequiredService<ISqlServerTableManager>();
        await tableManager.EnsureTablesExistAsync();

        CacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
    }

    public async Task DisposeAsync()
    {
        // Stop hosted services first
        if (ServiceProvider != null)
        {
            await StopHostedServicesAsync(ServiceProvider);
            await DisposeServiceProviderAsync(ServiceProvider);
        }
        // Do not dispose the shared container here; keep it alive for the entire test run.
    }

    protected static async Task StartHostedServicesAsync(IServiceProvider serviceProvider)
    {
        var hostedServices = serviceProvider.GetServices<IHostedService>();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    protected static async Task StopHostedServicesAsync(IServiceProvider serviceProvider)
    {
        var hostedServices = serviceProvider.GetServices<IHostedService>();
        foreach (var hostedService in hostedServices)
        {
            try
            {
                await hostedService.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Ignore exceptions during cleanup
            }
        }
    }

    protected static async Task DisposeServiceProviderAsync(IServiceProvider serviceProvider)
    {
        if (serviceProvider is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else if (serviceProvider is IDisposable disposable)
            disposable.Dispose();
    }

    protected static string CreateKeyPrefix(string prefix) => $"{prefix}:{Guid.NewGuid():N}:";
}

/// <summary>
/// Test-specific SQL Server Infrastructure extensions that work with test containers
/// </summary>
internal static class SqlServerInfrastructureTestExtensions
{
    public static IServiceCollection AddSqlServerInfrastructureForTests(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureSqlServer = null)
    {
        // Add core infrastructure
        services.AddCacheInfrastructure();

        // Configure SQL Server options
        if (configureSqlServer != null)
        {
            services.Configure(configureSqlServer);
        }

        // Register SQL Server-specific services
        services.TryAddSingleton<ISqlServerConnectionManager, SqlServerConnectionManager>();
        services.TryAddSingleton<ISqlServerSerializer, SqlServerSerializer>();
        services.TryAddSingleton<ISqlServerTableManager, SqlServerTableManager>();

        // Register the storage provider
        services.AddSingleton<SqlServerPersistentStorageProvider>();
        services.AddSingleton<IStorageProvider>(provider => provider.GetRequiredService<SqlServerPersistentStorageProvider>());

        // Register the backplane
        services.AddSingleton<SqlServerBackplane>();
        services.AddSingleton<IBackplane>(provider => provider.GetRequiredService<SqlServerBackplane>());

        // Register core cache services that may be needed
        services.TryAddSingleton<ICacheKeyGenerator, DefaultCacheKeyGenerator>();

        return services;
    }

    public static IServiceCollection AddSqlServerInfrastructureWithHealthChecksForTests(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureSqlServer = null,
        string healthCheckName = "sqlserver_infrastructure")
    {
        services.AddSqlServerInfrastructureForTests(configureSqlServer);

        // Add health checks
        services.AddHealthChecks()
            .AddCheck<MethodCache.Providers.SqlServer.HealthChecks.SqlServerInfrastructureHealthCheck>(healthCheckName);
        services.AddSingleton<MethodCache.Providers.SqlServer.HealthChecks.SqlServerInfrastructureHealthCheck>();

        return services;
    }

    public static IServiceCollection AddSqlServerHybridInfrastructureForTests(
        this IServiceCollection services,
        Action<SqlServerOptions>? configureSqlServer = null,
        Action<StorageOptions>? configureStorage = null)
    {
        // Add SQL Server infrastructure
        services.AddSqlServerInfrastructureForTests(configureSqlServer);

        // Add hybrid storage manager
        services.AddHybridStorageManager();

        return services;
    }
}

/// <summary>
/// Simple cache manager adapter that uses IStorageProvider for Infrastructure-based tests.
/// </summary>
internal class StorageProviderCacheManager : ICacheManager
{
    private readonly IStorageProvider _storageProvider;
    private readonly ICacheKeyGenerator _keyGenerator;

    public StorageProviderCacheManager(IStorageProvider storageProvider, ICacheKeyGenerator keyGenerator)
    {
        _storageProvider = storageProvider;
        _keyGenerator = keyGenerator;
    }

    public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool isIdempotent)
    {
        var key = keyGenerator.GenerateKey(methodName, args, settings);

        var cached = await _storageProvider.GetAsync<T>(key);
        if (cached != null)
        {
            return cached;
        }

        var value = await factory();
        if (value != null)
        {
            var expiration = settings.Duration ?? TimeSpan.FromMinutes(15);
            await _storageProvider.SetAsync(key, value, expiration, settings.Tags ?? new List<string>());
        }

        return value;
    }

    public Task InvalidateByTagsAsync(params string[] tags)
    {
        return Task.WhenAll(tags.Select(tag => _storageProvider.RemoveByTagAsync(tag)));
    }

    public Task InvalidateAsync(string methodName, params object[] args)
    {
        var key = _keyGenerator.GenerateKey(methodName, args, new CacheMethodSettings());
        return _storageProvider.RemoveAsync(key);
    }

    public Task InvalidateByKeysAsync(params string[] keys)
    {
        return Task.WhenAll(keys.Select(key => _storageProvider.RemoveAsync(key)));
    }

    public Task InvalidateByTagPatternAsync(string pattern)
    {
        // Basic implementation - remove by single tag
        return _storageProvider.RemoveByTagAsync(pattern);
    }

    public async ValueTask<T?> TryGetAsync<T>(string methodName, object[] args, CacheMethodSettings settings, ICacheKeyGenerator keyGenerator)
    {
        var key = keyGenerator.GenerateKey(methodName, args, settings);
        return await _storageProvider.GetAsync<T>(key);
    }

    public Task ClearAsync()
    {
        // Not supported by IStorageProvider interface
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_storageProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}