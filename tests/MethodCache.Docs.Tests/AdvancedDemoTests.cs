using MethodCache.Demo;
using MethodCache.HttpDemo;
using Xunit;

namespace MethodCache.Docs.Tests;

public class AdvancedDemoTests
{
    [Theory]
    [InlineData("expiration")]
    [InlineData("fail-safe")]
    [InlineData("conditional")]
    public async Task ConsoleDemo_AdvancedScenario_AllClaimsHold(string scenario)
    {
        var checks = await (scenario switch
        {
            "expiration" => AdvancedExamples.ExpirationAsync(),
            "fail-safe" => AdvancedExamples.FailSafeAsync(),
            "conditional" => AdvancedExamples.ConditionalAsync(),
            _ => throw new ArgumentException("Unknown example.", nameof(scenario))
        });
        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.True(check.Passed, check.Claim));
    }

    [Fact]
    public async Task HttpDemo_LocalServer_MaxAgeEtagNoStoreAndWriteInvalidationWork()
    {
        var checks = await HttpExamples.RunAsync();
        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.True(check.Passed, check.Claim));
    }
}
