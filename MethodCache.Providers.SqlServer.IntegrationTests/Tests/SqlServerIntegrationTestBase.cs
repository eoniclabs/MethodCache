using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using MethodCache.Core.Runtime.Defaults;
using MethodCache.Core.Storage;
using MethodCache.Infrastructure.Extensions;
using InfraStorageOptions = MethodCache.Infrastructure.Configuration.StorageOptions;
using MethodCache.Providers.SqlServer.Configuration;
using MethodCache.Providers.SqlServer.Extensions;
using MethodCache.Providers.SqlServer.Infrastructure;
using MethodCache.Providers.SqlServer.Services;
using Testcontainers.MsSql;
using Xunit;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading.Tasks;

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
    private static bool DockerAvailabilityChecked = false;
    private static bool DockerAvailable = false;

    public async Task InitializeAsync()
    {
        // Use a global timeout for test initialization to prevent hanging
        using var globalTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await InitLock.WaitAsync(globalTimeout.Token);
        try
        {
            if (SharedContainer == null)
            {
                // If an external SQL Server is provided, do not create a container
                var external = Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_URL")
                               ?? Environment.GetEnvironmentVariable("SQLSERVER_URL");
                if (string.IsNullOrWhiteSpace(external))
                {
                    // Check Docker availability once
                    if (!DockerAvailabilityChecked)
                    {
                        DockerAvailable = await CheckDockerAvailabilityAsync(globalTimeout.Token);
                        DockerAvailabilityChecked = true;
                    }

                    if (!DockerAvailable)
                    {
                        Console.WriteLine("Docker not available. To run integration tests:");
                        Console.WriteLine("1. Install and start Docker Desktop");
                        Console.WriteLine("2. Or set METHODCACHE_SQLSERVER_URL for external SQL Server");
                        Console.WriteLine("Example: export METHODCACHE_SQLSERVER_URL='Server=localhost;Database=TestCache;Trusted_Connection=true;'");
                        throw new InvalidOperationException("Docker not available for SQL Server integration tests. Set METHODCACHE_SQLSERVER_URL to use external SQL Server.");
                    }

                    try
                    {
                        var containerBuilder = new MsSqlBuilder()
                            // Use 2022 with explicit platform for Rosetta compatibility on Apple Silicon
                            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                            .WithPassword("YourStrong@Passw0rd")
                            .WithPortBinding(1433, true)
                            .WithEnvironment("ACCEPT_EULA", "Y")
                            .WithEnvironment("MSSQL_SA_PASSWORD", "YourStrong@Passw0rd")
                            .WithEnvironment("MSSQL_PID", "Developer") // Use Developer edition for full features
                            .WithEnvironment("MSSQL_COLLATION", "SQL_Latin1_General_CP1_CI_AS")
                            .WithWaitStrategy(Wait.ForUnixContainer()
                                .UntilPortIsAvailable(1433)
                                .UntilCommandIsCompleted("/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P YourStrong@Passw0rd -Q \"SELECT 1\" -C -l 3"))
                            .WithReuse(true)
                            .WithCleanUp(false); // Keep container alive for reuse

                        // Add platform specification for Apple Silicon compatibility
                        if (Environment.OSVersion.Platform == PlatformID.Unix)
                        {
                            var arch = RuntimeInformation.ProcessArchitecture;
                            if (arch == Architecture.Arm64)
                            {
                                Console.WriteLine("Detected Apple Silicon - using platform linux/amd64 for SQL Server compatibility");
                                containerBuilder = containerBuilder.WithCreateParameterModifier(p => p.Platform = "linux/amd64");
                            }
                        }

                        SharedContainer = containerBuilder.Build();

                        // Start with a timeout for Docker container startup, respecting global timeout
                        using var containerTimeout = CancellationTokenSource.CreateLinkedTokenSource(globalTimeout.Token);
                        containerTimeout.CancelAfter(TimeSpan.FromMinutes(2)); // Shorter timeout to respect global limit
                        Console.WriteLine("Starting SQL Server test container...");
                        var startTime = DateTime.UtcNow;
                        await SharedContainer.StartAsync(containerTimeout.Token);
                        var elapsed = DateTime.UtcNow - startTime;
                        Console.WriteLine($"SQL Server container started in {elapsed.TotalSeconds:F1}s");

                        // Disable forced TLS encryption to keep local integration tests working on macOS.
                        static async Task EnsureExecSuccessAsync(Task<DotNet.Testcontainers.Containers.ExecResult> execTask, string actionDescription)
                        {
                            var result = await execTask.ConfigureAwait(false);
                            if (result.ExitCode != 0)
                            {
                                throw new InvalidOperationException($"{actionDescription} failed. Exit code {result.ExitCode}. stderr: {result.Stderr}");
                            }
                        }

                        await EnsureExecSuccessAsync(
                            SharedContainer.ExecAsync(new[]
                            {
                                "/opt/mssql-tools18/bin/sqlcmd",
                                "-S", "localhost",
                                "-U", "sa",
                                "-P", "YourStrong@Passw0rd",
                                "-C",
                                "-Q", "EXEC sp_configure 'show advanced options', 1; RECONFIGURE;"
                            }, globalTimeout.Token),
                            "Enable advanced SQL Server options");

                        await EnsureExecSuccessAsync(
                            SharedContainer.ExecAsync(new[]
                            {
                                "/opt/mssql-tools18/bin/sqlcmd",
                                "-S", "localhost",
                                "-U", "sa",
                                "-P", "YourStrong@Passw0rd",
                                "-C",
                                "-Q", "EXEC sp_configure 'force encryption', 0; RECONFIGURE;"
                            }, globalTimeout.Token),
                            "Disable SQL Server forced encryption");
                    }
                    catch (Exception ex)
                    {
                        // If Docker startup fails, provide clear guidance
                        Console.WriteLine($"Docker container startup failed: {ex.Message}");
                        Console.WriteLine("To run tests faster, set METHODCACHE_SQLSERVER_URL to use external SQL Server");
                        throw new InvalidOperationException($"Failed to start SQL Server container: {ex.Message}. Set METHODCACHE_SQLSERVER_URL environment variable for external SQL Server.");
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
        string? rawContainerConnection = SharedContainer?.GetConnectionString();

        if (rawContainerConnection != null)
        {
            var rawBuilder = new SqlConnectionStringBuilder(rawContainerConnection);
            if (!string.IsNullOrEmpty(rawBuilder.Password))
            {
                rawBuilder.Password = "********";
            }
            Console.WriteLine($"Raw SQL Server container connection string: {rawBuilder.ConnectionString}");
        }

        SqlServerConnectionString = SharedContainer != null
            ? $"{rawContainerConnection};Connection Timeout=30;Command Timeout=30;Pooling=true;Min Pool Size=1;Max Pool Size=10"
            : (Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_URL")
               ?? Environment.GetEnvironmentVariable("SQLSERVER_URL")
               ?? throw new InvalidOperationException("No SQL Server connection available."));

        // Ensure local test connections work on macOS by disabling SQL Server TLS when using the container.
        var connectionBuilder = new SqlConnectionStringBuilder(SqlServerConnectionString);

        var hasEncryptSetting = SqlServerConnectionString.IndexOf("encrypt", System.StringComparison.OrdinalIgnoreCase) >= 0;
        var hasTrustServerCertificateSetting = SqlServerConnectionString.IndexOf("trustservercertificate", System.StringComparison.OrdinalIgnoreCase) >= 0;

        if (SharedContainer != null)
        {
            connectionBuilder.Encrypt = false;
            connectionBuilder.TrustServerCertificate = true;
        }
        else
        {
            if (!hasEncryptSetting)
            {
                connectionBuilder.Encrypt = false;
            }

            if (!hasTrustServerCertificateSetting)
            {
                connectionBuilder.TrustServerCertificate = true;
            }
        }

        SqlServerConnectionString = connectionBuilder.ConnectionString;

        var logBuilder = new SqlConnectionStringBuilder(SqlServerConnectionString);
        if (!string.IsNullOrEmpty(logBuilder.Password))
        {
            logBuilder.Password = "********";
        }
        Console.WriteLine($"Using SQL Server connection string: {logBuilder.ConnectionString}");

        var services = new ServiceCollection();
        // Reduce logging noise and overhead for CI speed
        services.AddLogging();

        // Use SQL Server Infrastructure setup optimized for testing
        services.AddSqlServerInfrastructure(options =>
        {
            options.ConnectionString = SqlServerConnectionString;
            options.EnableBackplane = true;
            options.EnableAutoTableCreation = true;
            options.CommandTimeoutSeconds = 10; // Shorter timeout for tests
            options.MaxRetryAttempts = 1; // Fewer retries for faster failures
            options.BackplanePollingInterval = TimeSpan.FromMilliseconds(100); // Faster polling for tests
            // Unique prefix per test class instance to avoid cross-test collisions
            options.KeyPrefix = $"test:{Guid.NewGuid():N}:";
            options.Schema = $"test_{Guid.NewGuid():N}".Replace("-", "");
            options.DefaultSerializer = SqlServerSerializerType.Json; // Use JSON for tests to avoid MessagePack issues
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

        // Initialize tables with timeout
        var tableManager = ServiceProvider.GetRequiredService<ISqlServerTableManager>();
        using var tableTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await tableManager.EnsureTablesExistAsync(tableTimeout.Token);

        CacheManager = ServiceProvider.GetRequiredService<ICacheManager>();
    }

    private static Task<bool> CheckDockerAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Quick check if Docker is available by trying to get Docker endpoint info
            var dockerEndpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
            Console.WriteLine($"Checking Docker availability at: {dockerEndpoint}");

            // This will fail fast if Docker is not available
            var testBuilder = new ContainerBuilder()
                .WithImage("hello-world")
                .WithCreateParameterModifier(c => c.AttachStdout = false);

            // Just try to build - don't actually start
            var container = testBuilder.Build();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Docker availability check failed: {ex.Message}");
            return Task.FromResult(false);
        }
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(timeout.Token);
        }
    }

    protected static async Task StopHostedServicesAsync(IServiceProvider serviceProvider)
    {
        var hostedServices = serviceProvider.GetServices<IHostedService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var hostedService in hostedServices)
        {
            try
            {
                await hostedService.StopAsync(timeout.Token);
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
        services.AddSingleton<IPersistentStorageProvider>(provider => provider.GetRequiredService<SqlServerPersistentStorageProvider>());

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
        Action<MethodCache.Infrastructure.Configuration.StorageOptions>? configureStorage = null)
    {
        // Add SQL Server infrastructure first
        services.AddSqlServerInfrastructureForTests(configureSqlServer);

        // Configure storage options
        if (configureStorage != null)
        {
            services.Configure(configureStorage);
        }

        // Add core infrastructure for hybrid manager
        services.AddCacheInfrastructure();

        // Register custom hybrid storage manager that uses SqlServer as L2
        services.AddScoped<HybridStorageManager>(provider =>
        {
            var memoryStorage = provider.GetRequiredService<IMemoryStorage>();
            var infraOptions = provider.GetRequiredService<IOptions<InfraStorageOptions>>();
            var coreOptions = Options.Create(new StorageOptions
            {
                L1MaxExpiration = infraOptions.Value.L1MaxExpiration,
                L2Enabled = infraOptions.Value.L2Enabled,
                L2DefaultExpiration = infraOptions.Value.L2DefaultExpiration,
                L3Enabled = infraOptions.Value.L3Enabled,
                L3DefaultExpiration = infraOptions.Value.L3DefaultExpiration,
                L3MaxExpiration = infraOptions.Value.L3MaxExpiration,
                MaxConcurrentL2Operations = infraOptions.Value.MaxConcurrentL2Operations,
                MaxConcurrentL3Operations = infraOptions.Value.MaxConcurrentL3Operations,
                EnableAsyncL2Writes = infraOptions.Value.EnableAsyncL2Writes,
                EnableAsyncL3Writes = infraOptions.Value.EnableAsyncL3Writes,
                EnableL3Promotion = infraOptions.Value.EnableL3Promotion,
                EnableBackplane = infraOptions.Value.EnableBackplane,
                EnableEfficientL1TagInvalidation = infraOptions.Value.EnableEfficientL1TagInvalidation,
                MaxTagMappings = infraOptions.Value.MaxTagMappings
            });
            var logger = provider.GetRequiredService<ILogger<HybridStorageManager>>();

            // Get SqlServer provider for L2/L3
            var sqlServerProvider = provider.GetRequiredService<SqlServerPersistentStorageProvider>();
            var backplane = provider.GetService<IBackplane>();

            return new HybridStorageManager(
                memoryStorage,
                coreOptions,
                logger,
                sqlServerProvider,  // L2 storage
                sqlServerProvider,  // L3 storage (same instance)
                backplane);
        });

        // Override IStorageProvider to use hybrid manager
        services.AddScoped<IStorageProvider>(provider =>
            provider.GetRequiredService<HybridStorageManager>());

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
            await _storageProvider.SetAsync(key, value, expiration, settings.Tags ?? new List<string>()).ConfigureAwait(false);
        }

        return value;
    }

    public Task InvalidateByTagsAsync(params string[] tags)
    {
        return Task.WhenAll(tags.Select(tag => _storageProvider.RemoveByTagAsync(tag).AsTask()));
    }

    public Task InvalidateAsync(string methodName, params object[] args)
    {
        var key = _keyGenerator.GenerateKey(methodName, args, new CacheMethodSettings());
        return _storageProvider.RemoveAsync(key).AsTask();
    }

    public Task InvalidateByKeysAsync(params string[] keys)
    {
        return Task.WhenAll(keys.Select(key => _storageProvider.RemoveAsync(key).AsTask()));
    }

    public Task InvalidateByTagPatternAsync(string pattern)
    {
        // Basic implementation - remove by single tag
        return _storageProvider.RemoveByTagAsync(pattern).AsTask();
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
