using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

/// <summary>
/// The runtime overrides over HTTP: a body written like a method's entry in appsettings sets an override for a method,
/// a group or every method, with the appsettings errors, and the audit function sees each change before it is made.
/// </summary>
public class AdminOverrideEndpointsTests : IAsyncLifetime
{
    private const string AuthHeader = "X-Test-User";
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    public interface IPricing
    {
        [Cache(Duration = "10m", Group = "pricing")] Task<int> GetPrice(int productId);
        [Cache(Duration = "10m", Group = "pricing")] Task<int> GetDiscount(int customerId);
        [Cache(Duration = "10m")] Task<int> GetStock(int productId);
    }

    public sealed class Pricing : IPricing
    {
        public int Loads;
        public Task<int> GetPrice(int productId) => Task.FromResult(Interlocked.Increment(ref Loads));
        public Task<int> GetDiscount(int customerId) => Task.FromResult(Interlocked.Increment(ref Loads));
        public Task<int> GetStock(int productId) => Task.FromResult(Interlocked.Increment(ref Loads));
    }

    private readonly List<(MethodCacheAdminActionKind Kind, string[] Targets)> _actions = [];
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Pricing _pricing = null!;
    private IPricing _cached = null!;
    private string _priceId = null!;
    private string _discountId = null!;
    private string _stockId = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        builder.Services.AddSingleton<Pricing>();
        builder.Services.AddSingleton<IPricing>(sp => sp.GetRequiredService<Pricing>());
        builder.Services.AddMethodCache();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireUserName("admin")));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMethodCacheAdmin("CacheAdmins", audit: (action, _) =>
        {
            lock (_actions) _actions.Add((action.Kind, action.Targets.ToArray()));
            return ValueTask.CompletedTask;
        });
        await _app.StartAsync();

        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Add(AuthHeader, "admin");
        _pricing = _app.Services.GetRequiredService<Pricing>();
        _cached = _app.Services.GetRequiredService<IPricing>();
        var keys = _app.Services.GetRequiredService<IMethodCacheKeys>();
        _priceId = keys.MethodId<IPricing>(p => p.GetPrice(0));
        _discountId = keys.MethodId<IPricing>(p => p.GetDiscount(0));
        _stockId = keys.MethodId<IPricing>(p => p.GetStock(0));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task PutForAMethod_SetsTheOverride_AndGetListsItInTheSameFormat()
    {
        var response = await PutAsync($$"""{ "MethodId": "{{_priceId}}", "Duration": "2m", "Tags": ["prices"], "Reason": "Incident 4211", "ExpiresAt": "2026-11-12T08:00:00Z" }""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var listed = JsonDocument.Parse(await _client.GetStringAsync("/methodcache/overrides"));
        var item = Assert.Single(listed.RootElement.EnumerateArray());
        Assert.Equal(_priceId, item.GetProperty("methodId").GetString());
        Assert.Equal("2m", item.GetProperty("duration").GetString());
        Assert.Equal(["prices"], item.GetProperty("tags").EnumerateArray().Select(static t => t.GetString()));
        Assert.Equal("Incident 4211", item.GetProperty("reason").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero), item.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("slidingExpiration").ValueKind);
        Assert.Equal([MethodCacheAdminActionKind.UpsertOverride], _actions.Select(static a => a.Kind));
        Assert.Equal([[_priceId]], _actions.Select(static a => a.Targets));
    }

    [Fact]
    public async Task PutForAGroup_TurnsCachingOffForItsMethods_AndDeleteForTheGroupTurnsItBackOn()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync("""{ "Group": "pricing", "Enabled": false }""")).StatusCode);

        await _cached.GetPrice(1);
        await _cached.GetPrice(1);
        await _cached.GetStock(1);
        await _cached.GetStock(1);
        Assert.Equal(3, _pricing.Loads);
        var overrides = await _client.GetFromJsonAsync<List<JsonElement>>("/methodcache/overrides");
        Assert.Equal([_discountId, _priceId], overrides!.Select(o => o.GetProperty("methodId").GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/methodcache/overrides?group=pricing")).StatusCode);

        Assert.Empty((await _client.GetFromJsonAsync<List<JsonElement>>("/methodcache/overrides"))!);
        Assert.Equal([MethodCacheAdminActionKind.UpsertOverride, MethodCacheAdminActionKind.RemoveOverride], _actions.Select(static a => a.Kind));
        Assert.Equal([["group:pricing"], ["group:pricing"]], _actions.Select(static a => a.Targets));
    }

    [Fact]
    public async Task PutForAll_AndDeleteAll()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await PutAsync("""{ "All": true, "Duration": "1m" }""")).StatusCode);
        Assert.Equal(3, (await _client.GetFromJsonAsync<List<JsonElement>>("/methodcache/overrides"))!.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/methodcache/overrides?methodId={Uri.EscapeDataString(_stockId)}")).StatusCode);
        Assert.Equal(2, (await _client.GetFromJsonAsync<List<JsonElement>>("/methodcache/overrides"))!.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/methodcache/overrides?all=true")).StatusCode);

        Assert.Empty((await _client.GetFromJsonAsync<List<JsonElement>>("/methodcache/overrides"))!);
        Assert.Equal(["*", _stockId, "*"], _actions.Select(static a => a.Targets.Single()));
    }

    [Theory]
    [InlineData("""{ "MethodId": "M", "Duration": "1 hour" }""", "'override:Duration' has value '1 hour', which is not a duration")]
    [InlineData("""{ "Duration": "2m" }""", "exactly one of MethodId, Group or \"All\": true")]
    [InlineData("""{ "Group": "pricing", "All": true, "Duration": "2m" }""", "exactly one of MethodId, Group or \"All\": true")]
    [InlineData("""{ "Group": "pricing", "Durations": "2m" }""", "'override:Durations' is not a setting MethodCache knows")]
    [InlineData("""{ "Group": "pricing", "Enabled": "" }""", "'override:Enabled' is empty")]
    [InlineData("""{ "Group": "pricing", "Notes": "why" }""", "// comment")]
    [InlineData("""{ "Group": "Pricing", "Enabled": false }""", "did you mean 'pricing'")]
    [InlineData("""{ "Group": "pricing" }""", "sets nothing")]
    [InlineData("""{ "Group": "pricing", "Tags": ["product:{productId}"] }""", "GetDiscount")]
    [InlineData("""{ "Group": "pricing", "Enabled": false, "ExpiresAt": "2026-11-11T11:00:00Z" }""", "has already passed")]
    [InlineData("""[1, 2]""", "not a JSON object")]
    [InlineData("""{ "Group": """, "not a JSON object")]
    public async Task InvalidPut_Is400WithTheReason_AndChangesNothing(string body, string reason)
    {
        var response = await PutAsync(body.Replace("\"M\"", JsonSerializer.Serialize(_priceId), StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await ReasonAsync(response), StringComparison.Ordinal);
        Assert.Empty((await _client.GetFromJsonAsync<List<JsonElement>>("/methodcache/overrides"))!);
        Assert.Empty(_actions);
    }

    [Fact]
    public async Task PutWithoutJson_Is415()
    {
        using var content = new StringContent("Duration=2m", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _client.PutAsync("/methodcache/overrides", content)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?all=false")]
    [InlineData("?group=pricing&all=true")]
    [InlineData("?group=nope")]
    [InlineData("?methodId=Nope.Missing")]
    [InlineData("?tag=x")]
    public async Task InvalidDelete_Is400_AndIsNotRecorded(string query)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync("/methodcache/overrides" + query)).StatusCode);
        Assert.Empty(_actions);
    }

    [Fact]
    public async Task Overrides_NeedTheAdminPolicy()
    {
        using var anonymous = _app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/methodcache/overrides")).StatusCode);
        using var content = new StringContent("""{ "All": true, "Enabled": false }""", Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync("/methodcache/overrides", content)).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<List<JsonElement>>("/methodcache/overrides"))!);
    }

    private static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return string.Join(" ", problem.RootElement.GetProperty("errors").EnumerateObject()
            .SelectMany(static error => error.Value.EnumerateArray().Select(static message => message.GetString())));
    }

    private Task<HttpResponseMessage> PutAsync(string json)
        => _client.PutAsync("/methodcache/overrides", new StringContent(json, Encoding.UTF8, "application/json"));

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
