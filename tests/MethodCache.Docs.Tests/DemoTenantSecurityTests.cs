using MethodCache.Demo;
using Xunit;

namespace MethodCache.Docs.Tests;

// Tenant isolation belongs to the security tests; docs/security.md links to this class.
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
