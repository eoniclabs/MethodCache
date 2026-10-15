using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MethodCache.Docs.Tests;

public class HttpCoreStartupTests
{
    [Fact]
    public async Task HttpWithoutExplicitCore_FailsAtHostStartup()
    {
        var builder = Host.CreateApplicationBuilder(); builder.Services.AddHttpClient("api").AddHttpCaching();
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.Contains("AddMethodCache", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpWithExplicitCore_EitherRegistrationOrder_Starts(bool coreFirst)
    {
        var builder = Host.CreateApplicationBuilder();
        if (coreFirst) builder.Services.AddMethodCache();
        builder.Services.AddHttpClient("api").AddHttpCaching();
        if (!coreFirst) builder.Services.AddMethodCache();
        using var host = builder.Build(); await host.StartAsync(); await host.StopAsync();
    }
}
