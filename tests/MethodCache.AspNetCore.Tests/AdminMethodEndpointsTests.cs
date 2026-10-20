using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.AspNetCore.Tests;

/// <summary>One cached method's entries through the admin endpoints: <c>entries?methodId=</c> and <c>invalidate-method</c>.</summary>
public class AdminMethodEndpointsTests : IAsyncLifetime
{
    private const string AuthHeader = "X-Test-User";

    public interface ICatalog
    {
        [Cache] Task<string> GetProduct(int id);
        [Cache] Task<string> GetProducts(int page);
    }

    public sealed class Catalog : ICatalog
    {
        public int Loads;
        public Task<string> GetProduct(int id) { Interlocked.Increment(ref Loads); return Task.FromResult("product-" + id); }
        public Task<string> GetProducts(int page) { Interlocked.Increment(ref Loads); return Task.FromResult("page-" + page); }
    }

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private Catalog _catalog = null!;
    private string _getProduct = null!;
    private string _getProducts = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Catalog>();
        builder.Services.AddSingleton<ICatalog>(sp => sp.GetRequiredService<Catalog>());
        builder.Services.AddMethodCache();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireUserName("admin")));

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapMethodCacheAdmin("CacheAdmins");
        await _app.StartAsync();

        _client = _app.GetTestClient();
        _client.DefaultRequestHeaders.Add(AuthHeader, "admin");

        _catalog = _app.Services.GetRequiredService<Catalog>();
        var catalog = _app.Services.GetRequiredService<ICatalog>();
        await catalog.GetProduct(1);
        await catalog.GetProduct(2);
        await catalog.GetProducts(1);

        var keys = _app.Services.GetRequiredService<IMethodCacheKeys>();
        _getProduct = keys.MethodId<ICatalog>(c => c.GetProduct(0));
        _getProducts = keys.MethodId<ICatalog>(c => c.GetProducts(0));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Entries_FilterByMethodId_ListOnlyThatMethodsEntries()
    {
        var entries = await _client.GetFromJsonAsync<List<CacheEntryInfo>>(
            "/methodcache/entries?methodId=" + Uri.EscapeDataString(_getProduct));

        Assert.Equal(2, entries!.Count);
        Assert.All(entries, entry => Assert.Equal(_getProduct, entry.MethodId));
    }

    [Theory]
    [InlineData("/methodcache/entries?methodId=")]
    [InlineData("/methodcache/entries?methodId=%20")]
    public async Task Entries_EmptyMethodId_IsRejected(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InvalidateMethod_RemovesOnlyThatMethodsEntries()
    {
        var response = await _client.PostAsJsonAsync("/methodcache/invalidate-method", new { methodId = _getProduct });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var left = await _client.GetFromJsonAsync<List<CacheEntryInfo>>("/methodcache/entries");
        var entry = Assert.Single(left!);
        Assert.Equal(_getProducts, entry.MethodId);

        var loads = _catalog.Loads;
        await _app.Services.GetRequiredService<ICatalog>().GetProduct(1);
        Assert.Equal(loads + 1, _catalog.Loads);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"methodId\":null}")]
    [InlineData("{\"methodId\":\" \"}")]
    [InlineData("{\"methodId\":\"[Unknown]Ns.IService.Method\"}")]
    [InlineData("{\"methodId\":\"*\"}")]
    [InlineData("{\"tags\":[\"products\"]}")]
    [InlineData("not json")]
    public async Task InvalidateMethod_BadOrUnknownMethodId_IsRejectedAndRemovesNothing(string body)
    {
        var response = await _client.PostAsync("/methodcache/invalidate-method", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var left = await _client.GetFromJsonAsync<List<CacheEntryInfo>>("/methodcache/entries");
        Assert.Equal(3, left!.Count);
    }

    [Fact]
    public async Task InvalidateMethod_TakesOnlyJson()
    {
        var response = await _client.PostAsync("/methodcache/invalidate-method",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["methodId"] = _getProduct }));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
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
