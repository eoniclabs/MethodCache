using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.AspNetCore.Tests;

/// <summary>The <c>audit</c> function of <c>MapMethodCacheAdmin</c>: who changed the cache, how, and when.</summary>
public class AdminAuditTests : IAsyncLifetime
{
    private const string AuthHeader = "X-Test-User";
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public interface IAuditedCatalog
    {
        [Cache(Tags = ["products"])] Task<string> GetProduct(int id);
    }

    public sealed class AuditedCatalog : IAuditedCatalog
    {
        public int Loads;
        public Task<string> GetProduct(int id) { Interlocked.Increment(ref Loads); return Task.FromResult("product-" + id); }
    }

    private readonly List<(MethodCacheAdminActionKind Kind, string[] Targets, string? User, DateTimeOffset Time)> _actions = [];
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private AuditedCatalog _catalog = null!;
    private IAuditedCatalog _cached = null!;
    private string _methodId = null!;
    private bool _auditFails;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        builder.Services.AddSingleton<AuditedCatalog>();
        builder.Services.AddSingleton<IAuditedCatalog>(sp => sp.GetRequiredService<AuditedCatalog>());
        builder.Services.AddMethodCache();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireUserName("admin")));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMethodCacheAdmin("CacheAdmins", audit: (action, _) =>
        {
            if (_auditFails) throw new InvalidOperationException("The audit log is unavailable.");
            lock (_actions) _actions.Add((action.Kind, action.Targets.ToArray(), action.User.Identity?.Name, action.Time));
            return ValueTask.CompletedTask;
        });
        await _app.StartAsync();

        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Add(AuthHeader, "admin");

        _catalog = _app.Services.GetRequiredService<AuditedCatalog>();
        _cached = _app.Services.GetRequiredService<IAuditedCatalog>();
        await _cached.GetProduct(1);
        await _cached.GetProduct(2);
        _methodId = _app.Services.GetRequiredService<IMethodCacheKeys>().MethodId<IAuditedCatalog>(c => c.GetProduct(0));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task EachChange_IsRecordedWithTheUserWhatItRemovesAndTheTime()
    {
        var entries = await _client.GetFromJsonAsync<List<CacheEntryInfo>>("/methodcache/entries");
        var storedKey = entries!.First().StoredKey;

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/methodcache/entry?storedKey={Uri.EscapeDataString(storedKey)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/methodcache/invalidate", new { Tags = new[] { "products", "prices" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/methodcache/invalidate-method", new { MethodId = _methodId })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsJsonAsync("/methodcache/clear", new { })).StatusCode);

        Assert.Equal(
            [MethodCacheAdminActionKind.RemoveEntry, MethodCacheAdminActionKind.InvalidateTags, MethodCacheAdminActionKind.InvalidateMethod, MethodCacheAdminActionKind.Clear],
            _actions.Select(a => a.Kind));
        Assert.Equal([[storedKey], ["products", "prices"], [_methodId], []], _actions.Select(a => a.Targets));
        Assert.All(_actions, a => Assert.Equal(("admin", Now), (a.User, a.Time)));
    }

    [Fact]
    public async Task ReadsAndRejectedRequests_AreNotRecorded()
    {
        await _client.GetAsync("/methodcache/stats");
        await _client.GetAsync("/methodcache/methods");
        await _client.GetAsync("/methodcache/entries");
        await _client.GetAsync("/methodcache/entry?storedKey=missing");

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync("/methodcache/entry?storedKey=")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/methodcache/invalidate", new { Tags = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/methodcache/invalidate-method", new { MethodId = "Nope.Missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/methodcache/clear", new { Tags = new[] { "products" } })).StatusCode);

        using var anonymous = _app.GetTestClient();
        using var anonymousRequest = new HttpRequestMessage(HttpMethod.Post, "/methodcache/clear") { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(anonymousRequest)).StatusCode);

        Assert.Empty(_actions);
    }

    [Fact]
    public async Task AThrowingAudit_FailsTheRequest_AndNothingChanges()
    {
        _auditFails = true;

        await Assert.ThrowsAnyAsync<Exception>(() => _client.PostAsJsonAsync("/methodcache/clear", new { }));
        await _cached.GetProduct(1);
        await _cached.GetProduct(2);

        Assert.Equal(2, _catalog.Loads);
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(AuthHeader, out var user)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
