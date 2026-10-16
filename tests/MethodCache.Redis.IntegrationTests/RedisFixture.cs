using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

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
            _container = new RedisBuilder("redis:7-alpine").Build();
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

    internal static Task WaitForSubscriberAsync(IConnectionMultiplexer connection)
        => WaitForSubscriberAsync(() => connection.GetSubscriber().PingAsync(), TimeProvider.System);

    // ConnectAsync can finish before pub/sub is connected. FailFast then cancels that connection's first
    // ping. Wait for actual readiness; the policy tests still use their separate subscription barrier.
    internal static async Task WaitForSubscriberAsync(Func<Task> ping, TimeProvider time)
    {
        var started = time.GetTimestamp();
        var budget = TimeSpan.FromSeconds(20);
        Exception? lastFailure = null;
        while (true)
        {
            var remaining = budget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("Redis subscription connection did not become ready within 20 seconds.", lastFailure);
            }

            try
            {
                await ping().WaitAsync(remaining, time);
                return;
            }
            catch (Exception ex) when (ex is OperationCanceledException or RedisConnectionException)
            {
                lastFailure = ex;
                var retryBudget = budget - time.GetElapsedTime(started);
                if (retryBudget > TimeSpan.Zero)
                {
                    var delay = TimeSpan.FromMilliseconds(10);
                    await Task.Delay(retryBudget < delay ? retryBudget : delay, time);
                }
            }
        }
    }

    private static bool InfrastructureRequired =>
        string.Equals(Environment.GetEnvironmentVariable("METHODCACHE_REQUIRE_INFRA_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
}

[CollectionDefinition(Name)]
public sealed class RedisCollection : ICollectionFixture<RedisFixture>
{
    public const string Name = "Redis";
}
