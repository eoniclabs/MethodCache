using System.Reflection;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Verifies that the registration shown in the README Quick Start produces cached services
/// without any extra registration calls.
/// </summary>
public class QuickStartRegistrationTests
{
    private const string QuickStartSource = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace QuickStart
{
    public interface IUserService
    {
        [Cache(Duration = ""00:10:00"", Tags = new[] { ""users"", ""user:{userId}"" })]
        Task<string> GetUserAsync(int userId);

        [CacheInvalidate(Tags = new[] { ""users"", ""user:{userId}"" })]
        Task UpdateUserAsync(int userId, string name);
    }

    public class UserService : IUserService
    {
        private int _calls;

        public int Calls => _calls;

        public Task<string> GetUserAsync(int userId)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(""user-"" + userId);
        }

        public Task UpdateUserAsync(int userId, string name) => Task.CompletedTask;
    }

    public static class Startup
    {
        public static void WithConfiguration(IServiceCollection services)
        {
            services.AddMethodCache(config =>
            {
                config.DefaultPolicy(policy => policy.WithDuration(TimeSpan.FromMinutes(10)));
            });
        }

        public static void WithoutConfiguration(IServiceCollection services)
        {
            services.AddMethodCache();
        }

        public static void WithExplicitAssembly(IServiceCollection services)
        {
            services.AddMethodCache(configure: null, typeof(IUserService).Assembly);
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Theory]
    [InlineData("WithConfiguration")]
    [InlineData("WithoutConfiguration")]
    [InlineData("WithExplicitAssembly")]
    public async Task AddMethodCache_FromApplicationAssembly_RegistersCachedDecorators(string startupMethod)
    {
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(
            QuickStartSource,
            $"QuickStartApp_{startupMethod}");
        var assembly = testAssembly.Assembly;

        var services = new ServiceCollection();
        var configure = assembly.GetType("QuickStart.Startup")!.GetMethod(startupMethod, BindingFlags.Public | BindingFlags.Static)!;
        configure.Invoke(null, new object[] { services });

        await using var provider = services.BuildServiceProvider();
        var interfaceType = assembly.GetType("QuickStart.IUserService")!;
        var implementationType = assembly.GetType("QuickStart.UserService")!;

        var service = provider.GetRequiredService(interfaceType);
        Assert.EndsWith("Decorator", service.GetType().Name);

        var getUser = interfaceType.GetMethod("GetUserAsync")!;
        var first = await (Task<string>)getUser.Invoke(service, new object[] { 42 })!;
        var second = await (Task<string>)getUser.Invoke(service, new object[] { 42 })!;

        Assert.Equal("user-42", first);
        Assert.Equal(first, second);

        var implementation = provider.GetRequiredService(implementationType);
        var calls = (int)implementationType.GetProperty("Calls")!.GetValue(implementation)!;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AddMethodCache_WithoutImplementation_DoesNotRegisterGeneratedDecoratorAsImplementation()
    {
        const string source = @"
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace QuickStart
{
    public interface IExternalService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetAsync(int id);
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services) => services.AddMethodCache();
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(source, "QuickStartApp_NoImplementation");
        var assembly = testAssembly.Assembly;

        var services = new ServiceCollection();
        assembly.GetType("QuickStart.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        Assert.DoesNotContain(services, d => d.ServiceType.Name == "IExternalServiceDecorator");
        Assert.DoesNotContain(services, d => d.ServiceType == assembly.GetType("QuickStart.IExternalService"));
    }

    [Fact]
    public async Task AddMethodCache_WithSeveralCachedInterfaces_RegistersGeneratedPolicySourceOnce()
    {
        const string source = @"
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace QuickStart
{
    public interface IFirstService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetAsync(int id);
    }

    public interface ISecondService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetAsync(int id);
    }

    public class FirstService : IFirstService
    {
        public Task<string> GetAsync(int id) => Task.FromResult(""first"");
    }

    public class SecondService : ISecondService
    {
        public Task<string> GetAsync(int id) => Task.FromResult(""second"");
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services) => services.AddMethodCache();
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(source, "QuickStartApp_SeveralInterfaces");
        var assembly = testAssembly.Assembly;

        var services = new ServiceCollection();
        assembly.GetType("QuickStart.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        await using var provider = services.BuildServiceProvider();
        var generatedSources = provider.GetServices<PolicySourceRegistration>()
            .Where(r => r.Source.GetType().Name == "GeneratedAttributePolicySource");
        Assert.Single(generatedSources);
    }

    [Fact]
    public async Task AddMethodCache_WithExistingRegistration_KeepsApplicationRegistration()
    {
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(QuickStartSource, "QuickStartApp_ExistingRegistration");
        var assembly = testAssembly.Assembly;
        var interfaceType = assembly.GetType("QuickStart.IUserService")!;
        var implementationType = assembly.GetType("QuickStart.UserService")!;

        var services = new ServiceCollection();
        services.AddScoped(interfaceType, implementationType);
        var existing = services[^1];

        assembly.GetType("QuickStart.Startup")!.GetMethod("WithoutConfiguration")!.Invoke(null, new object[] { services });

        Assert.Same(existing, services.Single(d => d.ServiceType == interfaceType));
    }
}
