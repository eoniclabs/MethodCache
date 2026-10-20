using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.AspNetCore.Tests;

public class MethodCacheAdminEndpointsTests : IAsyncLifetime
{
    private const string AuthHeader = "X-Test-User";

    private static readonly CacheRuntimePolicy UsersPolicy = CacheRuntimePolicy.FromPolicy(
        "Users",
        CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(10), Tags = new[] { "users" } },
        CachePolicyFields.Duration | CachePolicyFields.Tags);

    private static readonly CacheRuntimePolicy OrdersPolicy = CacheRuntimePolicy.FromPolicy(
        "Orders",
        CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(10), Tags = new[] { "orders" } },
        CachePolicyFields.Duration | CachePolicyFields.Tags);

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMethodCache();
        builder.Services.AddHealthChecks().AddMethodCache();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireUserName("admin")));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMethodCacheAdmin("CacheAdmins");
        _app.MapHealthChecks("/health");
        await _app.StartAsync();

        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Add(AuthHeader, "admin");

        var cache = _app.Services.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateFastAsync("users:1", "Users", () => Task.FromResult("Ada"), UsersPolicy);
        await cache.GetOrCreateFastAsync("users:2", "Users", () => Task.FromResult("Grace"), UsersPolicy);
        await cache.GetOrCreateFastAsync("orders:1", "Orders", () => Task.FromResult(42), OrdersPolicy);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Endpoints_RequireAnAuthenticatedUser()
    {
        using var anonymous = _app.GetTestClient();

        var response = await anonymous.GetAsync("/methodcache/stats");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Endpoints_RequireThePolicy_NotJustASignedInUser()
    {
        using var user = _app.GetTestClient();
        user.DefaultRequestHeaders.Add(AuthHeader, "someone");

        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/methodcache/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/methodcache/clear", new { })).StatusCode);
        Assert.Equal(3, (await _app.Services.GetRequiredService<ICacheAdministrator>().GetStatsAsync()).LocalMethodEntries);
    }

    [Fact]
    public void MapMethodCacheAdmin_RequiresAPolicyName()
    {
        Assert.Throws<ArgumentException>(() => _app.MapMethodCacheAdmin(" "));
    }

    [Fact]
    public async Task Stats_ReportEntries()
    {
        var stats = await _client.GetFromJsonAsync<CacheStats>("/methodcache/stats");

        Assert.NotNull(stats);
        Assert.Equal(3, stats.LocalMethodEntries);
        Assert.Equal(3, stats.Misses);
    }

    [Fact]
    public async Task Entries_FilterByStoredKeyPrefixAndTag_WithoutValues()
    {
        var byPrefix = await _client.GetFromJsonAsync<List<CacheEntryInfo>>("/methodcache/entries?storedKeyPrefix=users:");
        var byTag = await _client.GetFromJsonAsync<List<CacheEntryInfo>>("/methodcache/entries?tag=orders");

        Assert.Equal(new[] { "users:1", "users:2" }, byPrefix!.Select(e => e.StoredKey));
        var order = Assert.Single(byTag!);
        Assert.Equal("orders:1", order.StoredKey);
        Assert.Equal(typeof(int).FullName, order.ValueType);
        Assert.Contains("orders", order.Tags);
        Assert.NotNull(order.TimeToLive);
        Assert.True(order.TimeToLive <= TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Entries_RejectAnOutOfRangeLimit()
    {
        var response = await _client.GetAsync("/methodcache/entries?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Entry_CanBeReadAndRemoved()
    {
        var found = await _client.GetAsync("/methodcache/entry?storedKey=users:1");
        var removed = await _client.DeleteAsync("/methodcache/entry?storedKey=users:1");
        var gone = await _client.GetAsync("/methodcache/entry?storedKey=users:1");
        var removedAgain = await _client.DeleteAsync("/methodcache/entry?storedKey=users:1");

        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removedAgain.StatusCode);
    }

    [Fact]
    public async Task Invalidate_RemovesEntriesWithTheTag()
    {
        var response = await _client.PostAsJsonAsync("/methodcache/invalidate", new { Tags = new[] { "users" } });
        var remaining = await _client.GetFromJsonAsync<List<CacheEntryInfo>>("/methodcache/entries");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(new[] { "orders:1" }, remaining!.Select(e => e.StoredKey));
    }

    [Fact]
    public async Task Invalidate_RejectsAnEmptyTagList()
    {
        var response = await _client.PostAsJsonAsync("/methodcache/invalidate", new { Tags = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Clear_RemovesEverything()
    {
        var response = await _client.PostAsJsonAsync("/methodcache/clear", new { });
        var stats = await _client.GetFromJsonAsync<CacheStats>("/methodcache/stats");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, stats!.LocalMethodEntries);
    }

    [Fact]
    public async Task DeleteEntries_IsNoLongerARoute_AndRemovesNothing()
    {
        // DELETE entries?tag=orders, the natural guess after GET entries?tag=orders, used to empty the whole cache
        var response = await _client.DeleteAsync("/methodcache/entries?tag=orders");
        var stats = await _client.GetFromJsonAsync<CacheStats>("/methodcache/stats");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(3, stats!.LocalMethodEntries);
    }

    [Fact]
    public async Task Methods_ReturnsAList()
    {
        var response = await _client.GetAsync("/methodcache/methods");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<List<CachedMethodInfo>>());
    }

    [Fact]
    public async Task HealthCheck_ReportsHealthyWithStatistics()
    {
        var report = await _app.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal("methodcache", entry.Key);
        Assert.Equal(HealthStatus.Healthy, entry.Value.Status);
        Assert.Equal(3L, entry.Value.Data["localMethodEntries"]);
        Assert.False(entry.Value.Data.ContainsKey("distributedCacheAvailable"));
    }

    [Fact]
    public async Task HealthCheck_IsDegradedWhileTheDistributedCacheIsUnreachable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCacheDistributedCache(_ => new UnreachableDistributedCache());
        services.AddHealthChecks().AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        // Before any cached call has run into the outage
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(HealthStatus.Degraded, entry.Value.Status);
        Assert.Equal(false, entry.Value.Data["distributedCacheAvailable"]);
    }

    [Fact]
    public async Task HealthCheck_IsHealthyWhenTheDistributedCacheAnswers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMethodCacheDistributedCache(_ => new MethodCache.TestSupport.AtomicTestCache());
        services.AddHealthChecks().AddMethodCache();
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        var entry = Assert.Single(report.Entries);
        Assert.Equal(HealthStatus.Healthy, entry.Value.Status);
        Assert.Equal(true, entry.Value.Data["distributedCacheAvailable"]);
    }

    private sealed class UnreachableDistributedCache : MethodCache.Extensibility.IAtomicInvalidationStore
    {
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default) => throw new InvalidOperationException("offline");
        public byte[]? Get(string key) => throw Unreachable();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromException<byte[]?>(Unreachable());
        public void Refresh(string key) => throw Unreachable();
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.FromException(Unreachable());
        public void Remove(string key) => throw Unreachable();
        public Task RemoveAsync(string key, CancellationToken token = default) => Task.FromException(Unreachable());
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => throw Unreachable();
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default) => Task.FromException(Unreachable());

        private static Exception Unreachable() => new InvalidOperationException("unreachable");
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(AuthHeader, out var user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user.ToString()) }, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
