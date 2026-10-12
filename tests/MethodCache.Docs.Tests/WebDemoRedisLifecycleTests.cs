using Xunit;
using Xunit.Abstractions;

namespace MethodCache.Docs.Tests;

public class WebDemoRedisLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OptionalInfrastructure_ContainerConstructionFails_InitializationAndCleanupSucceed()
    {
        var fixture = new WebDemoRedisTests(output,
            () => throw new InvalidOperationException("Docker is unavailable."), infrastructureRequired: false);

        await fixture.InitializeAsync();

        await Assert.ThrowsAsync<SkipException>(fixture.ConnectAsync);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task RequiredInfrastructure_ContainerConstructionFails_InitializationFailsAndCleanupSucceeds()
    {
        var fixture = new WebDemoRedisTests(output,
            () => throw new InvalidOperationException("Docker is unavailable."), infrastructureRequired: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.InitializeAsync);

        Assert.Equal("Docker is unavailable.", error.Message);
        await fixture.DisposeAsync();
    }
}
