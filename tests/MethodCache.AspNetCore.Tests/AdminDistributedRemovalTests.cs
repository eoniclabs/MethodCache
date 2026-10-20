using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using MethodCache.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.AspNetCore.Tests;

/// <summary>
/// With a distributed cache, an entry this instance never loaded still lives in Redis or SQL Server and on other
/// instances; removing it there is a success.
/// </summary>
public class AdminDistributedRemovalTests
{
    private const string AuthHeader = "X-Test-User";

    private static readonly CacheRuntimePolicy Policy = CacheRuntimePolicy.FromPolicy(
        "Users",
        CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(10) },
        CachePolicyFields.Duration);

    [Fact]
    public async Task DeleteEntry_KeyOnlyInTheDistributedCache_RemovesItAndAnswers204()
    {
        var shared = new AtomicTestCache();

        // Node B loads the value: it is now in B's memory and in the shared distributed cache.
        var nodeB = new ServiceCollection();
        nodeB.AddLogging();
        nodeB.AddMethodCache();
        nodeB.AddMethodCacheDistributedCache(_ => shared);
        await using var providerB = nodeB.BuildServiceProvider();
        await providerB.GetRequiredService<ICacheManager>().GetOrCreateFastAsync("users:1", "Users", () => Task.FromResult("Ada"), Policy);

        // Node A has never loaded it.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMethodCache();
        builder.Services.AddMethodCacheDistributedCache(_ => shared);
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireUserName("admin")));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMethodCacheAdmin("CacheAdmins");
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(AuthHeader, "admin");

        var response = await client.DeleteAsync("/methodcache/entry?key=users:1");

        // The removal did happen in the distributed cache: node A now has to load the value again.
        var loads = 0;
        await app.Services.GetRequiredService<ICacheManager>().GetOrCreateFastAsync("users:1", "Users", () =>
        {
            loads++;
            return Task.FromResult("Ada");
        }, Policy);
        Assert.Equal(1, loads);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData(false, "remove")]
    [InlineData(false, "tag")]
    [InlineData(false, "clear")]
    [InlineData(true, "remove")]
    [InlineData(true, "tag")]
    [InlineData(true, "clear")]
    public async Task UnconfirmedMutation_Answers503AndRetainsRecovery(bool httpOnly, string operation)
    {
        var backend = HttpEpochTestCache.Create();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (httpOnly) builder.Services.AddSingleton<ICacheManager>(new MethodCache.NoOpCacheManager());
        builder.Services.AddMethodCache();
        builder.Services.AddMethodCacheDistributedCache(_ => backend);
        if (httpOnly) builder.Services.AddHttpClient("admin").AddHttpCaching();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(options => options.AddPolicy("CacheAdmins", policy => policy.RequireUserName("admin")));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMethodCacheAdmin("CacheAdmins");
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(AuthHeader, "admin");
        var key = "method-key";
        if (httpOnly && operation == "remove")
        {
            var store = app.Services.GetRequiredService<MethodCache.Core.Runtime.Http.HttpResponseStore>();
            key = MethodCache.Core.Runtime.Http.HttpResponseStore.Identity("representation");
            store.ObserveRepresentation("representation", new(key, "url-target", "admin", 2, 100, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), false));
        }
        backend.FailStamp = true;
        backend.FailAdvance = true;
        using var response = operation switch
        {
            "remove" => await client.DeleteAsync("/methodcache/entry?key=" + Uri.EscapeDataString(key)),
            "clear" => await client.DeleteAsync("/methodcache/entries"),
            _ => await client.PostAsync("/methodcache/invalidate", new StringContent("{\"tags\":[\"users\"]}", System.Text.Encoding.UTF8, "application/json"))
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(app.Services.GetRequiredService<ICacheAdministrator>().GetInvalidationQueues().Queued > 0);
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
