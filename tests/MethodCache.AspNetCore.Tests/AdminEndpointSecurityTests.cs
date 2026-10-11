using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.AspNetCore.Tests;

/// <summary>
/// Pins the threats listed for MethodCache.AspNetCore in docs/security.md: every admin endpoint needs the
/// authorization policy, and the endpoints reject bad input without touching the cache.
/// </summary>
/// <remarks>
/// The endpoints are read from the app's route table, not from a hand-written list, so an endpoint added to
/// <c>MapMethodCacheAdmin</c> without authorization fails <see cref="EveryAdminEndpoint_AnswersByWhoIsCalling"/>.
/// </remarks>
public class AdminEndpointSecurityTests : IAsyncLifetime
{
    private const string AuthHeader = "X-Test-User";
    private const string Policy = "CacheAdmins";
    private const string Prefix = "/ops/cache";
    private const string SecretValue = "secret-value-0451";

    private static readonly CacheRuntimePolicy UsersPolicy = CacheRuntimePolicy.FromPolicy(
        "Users",
        CachePolicy.Empty with { Duration = TimeSpan.FromMinutes(10), Tags = new[] { "users" } },
        CachePolicyFields.Duration | CachePolicyFields.Tags);

    private WebApplication _app = null!;

    /// <summary>The admin endpoints as mapped, read from the route table.</summary>
    private IReadOnlyList<(string Method, string Path, RouteEndpoint Endpoint)> AdminEndpoints =>
        _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" })
                .Select(method => (method, "/" + e.RoutePattern.RawText!.TrimStart('/'), e)))
            .OrderBy(e => e.Item2, StringComparer.Ordinal).ThenBy(e => e.method, StringComparer.Ordinal)
            .ToList();

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMethodCache();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy(Policy, p => p.RequireRole("Admin")));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMethodCacheAdmin(Policy, Prefix);
        await _app.StartAsync();

        var cache = _app.Services.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateFastAsync("users:1", "Users", () => Task.FromResult(SecretValue), UsersPolicy);
        await cache.GetOrCreateFastAsync("users:2", "Users", () => Task.FromResult("Grace"), UsersPolicy);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public void AdminEndpoints_AreTheDocumentedSet()
    {
        // A new endpoint must be added here, to the README's table and to the threat list in docs/security.md
        var mapped = AdminEndpoints.Select(e => $"{e.Method} {e.Path}").ToArray();

        Assert.Equal(
            new[]
            {
                "DELETE /ops/cache/entries",
                "GET /ops/cache/entries",
                "DELETE /ops/cache/entry",
                "GET /ops/cache/entry",
                "POST /ops/cache/invalidate",
                "GET /ops/cache/methods",
                "GET /ops/cache/stats",
            },
            mapped);
    }

    [Fact]
    public void EveryAdminEndpoint_RequiresThePolicy_AndNoneAllowsAnonymous()
    {
        Assert.NotEmpty(AdminEndpoints);
        Assert.All(AdminEndpoints, e =>
        {
            Assert.Null(e.Endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains(e.Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), a => a.Policy == Policy);
        });
    }

    [Fact]
    public async Task EveryAdminEndpoint_AnswersByWhoIsCalling()
    {
        // The authorization matrix: anonymous 401, signed in without the role 403, admin anything else
        foreach (var (method, path, _) in AdminEndpoints)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(method, path, user: null));
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(method, path, user: "someone"));
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(method, path, user: "someone", role: "Reader"));

            var admin = await SendAsync(method, path, user: "root", role: "Admin");
            Assert.True(admin is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{method} {path} refused an admin: {admin}");
        }
    }

    [Fact]
    public async Task RefusedCalls_LeaveTheCacheAsItWas()
    {
        foreach (var (method, path, _) in AdminEndpoints.Where(e => e.Method is "DELETE" or "POST"))
        {
            await SendAsync(method, path, user: null);
            await SendAsync(method, path, user: "someone");
        }

        Assert.Equal(2, (await Administrator.GetStatsAsync()).Entries);
    }

    [Theory]
    [InlineData("GET", "/entry")]                 // key missing
    [InlineData("DELETE", "/entry")]              // key missing
    [InlineData("GET", "/entries?limit=0")]
    [InlineData("GET", "/entries?limit=-1")]
    [InlineData("GET", "/entries?limit=1001")]
    [InlineData("GET", "/entries?limit=2147483648")]
    [InlineData("GET", "/entries?limit=ten")]
    public async Task BadQueries_AreRejected(string method, string path)
    {
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(method, Prefix + path, user: "root", role: "Admin"));
        Assert.Equal(2, (await Administrator.GetStatsAsync()).Entries);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"tags\":null}")]
    [InlineData("{\"tags\":[]}")]
    [InlineData("{\"tags\":[\"users\",\" \"]}")]
    [InlineData("{\"tags\":[\"users\",null]}")]
    [InlineData("{\"tags\":\"users\"}")]
    [InlineData("not json")]
    public async Task Invalidate_RejectsABadBody_AndRemovesNothing(string body)
    {
        using var client = Client("root", "Admin");

        var response = await client.PostAsync(Prefix + "/invalidate", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, (await Administrator.GetStatsAsync()).Entries);
    }

    [Theory]
    [InlineData("application/x-www-form-urlencoded", "tags=users")]
    [InlineData("text/plain", "{\"tags\":[\"users\"]}")]
    [InlineData("multipart/form-data; boundary=x", "--x\r\nContent-Disposition: form-data; name=\"tags\"\r\n\r\nusers\r\n--x--")]
    public async Task Invalidate_AcceptsOnlyJson_SoAPlainCrossSiteFormCannotCallIt(string contentType, string body)
    {
        // A browser sends these content types cross-site without a CORS preflight; application/json needs one
        using var client = Client("root", "Admin");
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);

        var response = await client.PostAsync(Prefix + "/invalidate", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(2, (await Administrator.GetStatsAsync()).Entries);
    }

    [Fact]
    public async Task EntryResponses_NeverContainCachedValues()
    {
        using var client = Client("root", "Admin");

        var list = await client.GetStringAsync(Prefix + "/entries");
        var one = await client.GetStringAsync(Prefix + "/entry?key=users:1");

        Assert.Contains("users:1", list);
        Assert.DoesNotContain(SecretValue, list);
        Assert.DoesNotContain(SecretValue, one);
        var entry = await client.GetFromJsonAsync<Dictionary<string, object>>(Prefix + "/entry?key=users:1");
        Assert.Equal(
            new[] { "age", "key", "tags", "timeToLive", "valueType", "weight" },
            entry!.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("users:*")]
    [InlineData("*")]
    [InlineData("users:1\nusers:2")]
    public async Task EntryKey_IsMatchedLiterally_NotAsAPattern(string key)
    {
        using var client = Client("root", "Admin");

        var response = await client.DeleteAsync(Prefix + "/entry?key=" + Uri.EscapeDataString(key));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(2, (await Administrator.GetStatsAsync()).Entries);
    }

    [Fact]
    public async Task Invalidate_MatchesTagsLiterally_NotAsAPattern()
    {
        using var client = Client("root", "Admin");

        var response = await client.PostAsJsonAsync(Prefix + "/invalidate", new InvalidateRequest(new[] { "user*", "*" }));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, (await Administrator.GetStatsAsync()).Entries);
    }

    private ICacheAdministrator Administrator => _app.Services.GetRequiredService<ICacheAdministrator>();

    private HttpClient Client(string? user, string? role = null)
    {
        var client = _app.GetTestClient();
        if (user != null)
        {
            client.DefaultRequestHeaders.Add(AuthHeader, role == null ? user : user + "|" + role);
        }

        return client;
    }

    private async Task<HttpStatusCode> SendAsync(string method, string path, string? user, string? role = null)
    {
        using var client = Client(user, role);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new InvalidateRequest(new[] { "users" }));
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
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
            if (!Request.Headers.TryGetValue(AuthHeader, out var header))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // "name" or "name|role"
            var parts = header.ToString().Split('|');
            var claims = new List<Claim> { new(ClaimTypes.Name, parts[0]) };
            if (parts.Length > 1)
            {
                claims.Add(new Claim(ClaimTypes.Role, parts[1]));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
