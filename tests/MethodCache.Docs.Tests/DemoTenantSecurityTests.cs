using MethodCache.Demo;
using Xunit;

namespace MethodCache.Docs.Tests;

public class DemoTenantSecurityTests
{
    [Fact]
    public async Task TenantExample_SameProductId_KeepsValuesAndInvalidationSeparate()
    {
        var checks = await AdvancedExamples.TenantsAsync();
        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.True(check.Passed, check.Claim));
    }
}
