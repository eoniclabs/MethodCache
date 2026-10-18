using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using MethodCache.WebDemo;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.Docs.Tests;

public class WebDemoSecurityTests
{
    [Fact]
    public async Task Production_DemoResetAndQueryCountersAreNotExposed()
    {
        await using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/demo/queries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/demo/reset", null)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/methodcache/stats", HttpStatusCode.OK)]
    [InlineData("GET", "/methodcache/methods", HttpStatusCode.OK)]
    [InlineData("GET", "/methodcache/entries", HttpStatusCode.OK)]
    [InlineData("GET", "/methodcache/entry?key=missing", HttpStatusCode.NotFound)]
    [InlineData("DELETE", "/methodcache/entry?key=missing", HttpStatusCode.NotFound)]
    [InlineData("DELETE", "/methodcache/entries", HttpStatusCode.NoContent)]
    [InlineData("POST", "/methodcache/invalidate", HttpStatusCode.NoContent)]
    public async Task AdminEndpoints_ProductionChallengesAnonymous_DevelopmentAllowsAnonymous(
        string method, string path, HttpStatusCode developmentStatus)
    {
        foreach (var environment in new[] { "Production", "Development" })
        {
            await using var factory = CreateFactory(environment);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method == "POST") request.Content = JsonContent.Create(new { tags = new[] { "products" } });

            using var response = await client.SendAsync(request);

            Assert.Equal(environment == "Production" ? HttpStatusCode.Unauthorized : developmentStatus, response.StatusCode);
        }
    }

    [Fact]
    public async Task Production_PublicEndpointsRemainAvailable()
    {
        await using var factory = CreateFactory("Production");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Theory]
    [InlineData("Reader", HttpStatusCode.Forbidden)]
    [InlineData("Admin", HttpStatusCode.OK)]
    public async Task Production_AdminPolicyRequiresTheAdminRole(string role, HttpStatusCode expected)
    {
        await using var factory = CreateFactory("Production");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "operator"), new Claim(ClaimTypes.Role, role) }, "Cookies");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Cookies");
        client.DefaultRequestHeaders.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");

        using var response = await client.GetAsync("/methodcache/stats");

        Assert.Equal(expected, response.StatusCode);
    }

    private static DemoWebFactory CreateFactory(string environment) => new(environment);
}
