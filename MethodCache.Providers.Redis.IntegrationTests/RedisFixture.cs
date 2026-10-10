using Testcontainers.Redis;
using Xunit;

namespace MethodCache.Providers.Redis.IntegrationTests;

/// <summary>
/// Finds a Redis to test against: <c>METHODCACHE_REDIS_URL</c> if set, otherwise a Docker container. Without
/// either the tests are skipped, unless <c>METHODCACHE_REQUIRE_INFRA_TESTS</c> is <c>true</c>.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string? Configuration { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        Configuration = Environment.GetEnvironmentVariable("METHODCACHE_REDIS_URL");
        if (!string.IsNullOrEmpty(Configuration))
        {
            return;
        }

        try
        {
            _container = new RedisBuilder().WithImage("redis:7-alpine").Build();
            await _container.StartAsync();
            Configuration = _container.GetConnectionString();
        }
        catch (Exception ex) when (!InfrastructureRequired)
        {
            UnavailableReason = $"No Redis: set METHODCACHE_REDIS_URL or start Docker ({ex.GetType().Name}).";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    public string RequireConfiguration()
    {
        Skip.If(UnavailableReason != null, UnavailableReason);
        return Configuration!;
    }

    private static bool InfrastructureRequired =>
        string.Equals(Environment.GetEnvironmentVariable("METHODCACHE_REQUIRE_INFRA_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
