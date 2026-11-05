using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// appsettings values that would otherwise be ignored or misread are rejected, as the configuration docs say.
/// </summary>
public class AppsettingsValidationTests
{
    [Theory]
    [InlineData("MethodCache:Methods:Sample.Type.Method:Durration", "5m", "not a setting")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:duration_seconds", "300", "not a setting")]
    [InlineData("MethodCache:Defaults:Ttl", "5m", "not a setting")]
    [InlineData("MethodCache:FailSafe:Durration", "1h", "not a setting")]
    [InlineData("MethodCache:Defualts:Duration", "5m", "not a setting")]
    [InlineData("MethodCache:Servcies:Sample.Type.Method:Duration", "5m", "not a setting")]
    [InlineData("MethodCache:Defaults", "5m", "single value")]
    [InlineData("MethodCache:Methods", "typo", "single value")]
    [InlineData("MethodCache:Methods:Sample.Type.Method", "5m", "single value")]
    [InlineData("MethodCache:Defaults:Duration:Value", "5m", "nested")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:Version:Major", "2", "nested")]
    [InlineData("MethodCache:FailSafe:Timeout:Value", "1s", "nested")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:Tags:0:Name", "users", "nested")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:Metadata", "owner", "single value")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:Metadata:Owner:Name", "team", "nested")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:Tags", "users", "list")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:FailSafe:Duration", "1h", "top of the section")]
    [InlineData("MethodCache:Defaults:FailSafe:Duration", "1h", "top of the section")]
    [InlineData("MethodCache:Methods:Sample.Type.Method:Duration", "300", "a positive number and a unit")]
    [InlineData("MethodCache:Defaults:SlidingExpiration", "5", "a positive number and a unit")]
    [InlineData("MethodCache:Defaults:Duration", "00:05:00", "Write \"5m\".")]
    public void SuspiciousValue_IsRejected(string key, string value, string message)
    {
        using var provider = Build(new() { [key] = value });

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == "Sample.Type.Method"));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void SettingNames_AreCaseInsensitive()
    {
        using var provider = Build(new()
        {
            ["MethodCache:Methods:Sample.Type.Method:duration"] = "5m",
            ["MethodCache:Methods:Sample.Type.Method:tags:0"] = "users",
            ["MethodCache:Methods:Sample.Type.Method:failsafe"] = "off",
        });

        var policy = provider.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == "Sample.Type.Method").Options;
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Duration);
        Assert.Equal(new[] { "users" }, policy.Tags);
    }

    // Environment variable names can't hold '.', '(' or '<', so a method id goes in MethodId under an index
    [Fact]
    public void MethodIdProperty_FromEnvironmentVariables_ConfiguresAnOverload()
    {
        const string prefix = "MCDOCS_METHODID_";
        Environment.SetEnvironmentVariable(prefix + "MethodCache__Methods__0__MethodId", "[MethodCache.Docs.Tests]MyApp.IUserService.FindAsync(int)");
        Environment.SetEnvironmentVariable(prefix + "MethodCache__Methods__0__Duration", "2m");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var services = new ServiceCollection();
            services.AddMethodCache(methodCache => methodCache.FromConfiguration(configuration));
            using var provider = services.BuildServiceProvider();

            var policy = provider.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == "[MethodCache.Docs.Tests]MyApp.IUserService.FindAsync(int)").Options;
            Assert.Equal(TimeSpan.FromMinutes(2), policy.Duration);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "MethodCache__Methods__0__MethodId", null);
            Environment.SetEnvironmentVariable(prefix + "MethodCache__Methods__0__Duration", null);
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddMethodCache(methodCache => methodCache.FromConfiguration(configuration));
        return services.BuildServiceProvider();
    }
}
