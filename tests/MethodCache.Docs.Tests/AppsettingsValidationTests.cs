using MethodCache.Policies;
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
    [InlineData("MethodCache:Services:Sample.Type.Method:Durration", "00:05:00", "not a setting")]
    [InlineData("MethodCache:Services:Sample.Type.Method:duration_seconds", "300", "not a setting")]
    [InlineData("MethodCache:Defaults:Ttl", "00:05:00", "not a setting")]
    [InlineData("MethodCache:FailSafe:Durration", "01:00:00", "not a setting")]
    [InlineData("MethodCache:Defualts:Duration", "00:05:00", "not a setting")]
    [InlineData("MethodCache:Servcies:Sample.Type.Method:Duration", "00:05:00", "not a setting")]
    [InlineData("MethodCache:Defaults", "00:05:00", "single value")]
    [InlineData("MethodCache:Services", "typo", "single value")]
    [InlineData("MethodCache:Services:Sample.Type.Method", "00:05:00", "single value")]
    [InlineData("MethodCache:Services:Sample.Type.Method:Tags", "users", "list")]
    [InlineData("MethodCache:Services:Sample.Type.Method:FailSafe:Duration", "01:00:00", "top of the section")]
    [InlineData("MethodCache:Defaults:FailSafe:Duration", "01:00:00", "top of the section")]
    [InlineData("MethodCache:Services:Sample.Type.Method:Duration", "300", "00:05:00")]
    [InlineData("MethodCache:Defaults:SlidingExpiration", "5", "00:05:00")]
    public void SuspiciousValue_IsRejected(string key, string value, string message)
    {
        using var provider = Build(new() { [key] = value });

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPolicyRegistry>().GetPolicy("Sample.Type.Method"));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void SettingNames_AreCaseInsensitive()
    {
        using var provider = Build(new()
        {
            ["MethodCache:Services:Sample.Type.Method:duration"] = "00:05:00",
            ["MethodCache:Services:Sample.Type.Method:tags:0"] = "users",
            ["MethodCache:Services:Sample.Type.Method:failsafe"] = "off",
        });

        var policy = provider.GetRequiredService<IPolicyRegistry>().GetPolicy("Sample.Type.Method").Policy;
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Duration);
        Assert.Equal(new[] { "users" }, policy.Tags);
    }

    // Environment variable names can't hold '.', '(' or '<', so a method id goes in MethodId under an index
    [Fact]
    public void MethodIdProperty_FromEnvironmentVariables_ConfiguresAnOverload()
    {
        const string prefix = "MCDOCS_METHODID_";
        Environment.SetEnvironmentVariable(prefix + "MethodCache__Services__0__MethodId", "MyApp.IUserService.FindAsync(int)");
        Environment.SetEnvironmentVariable(prefix + "MethodCache__Services__0__Duration", "00:02:00");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables(prefix).Build();
            var services = new ServiceCollection();
            services.AddMethodCacheFromConfiguration(configuration);
            using var provider = services.BuildServiceProvider();

            var policy = provider.GetRequiredService<IPolicyRegistry>().GetPolicy("MyApp.IUserService.FindAsync(int)").Policy;
            Assert.Equal(TimeSpan.FromMinutes(2), policy.Duration);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "MethodCache__Services__0__MethodId", null);
            Environment.SetEnvironmentVariable(prefix + "MethodCache__Services__0__Duration", null);
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddMethodCacheFromConfiguration(configuration);
        return services.BuildServiceProvider();
    }
}
