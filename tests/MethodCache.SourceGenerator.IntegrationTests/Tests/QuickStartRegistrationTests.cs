using System.Reflection;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
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
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
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
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task AddMethodCache_WithExistingScopedRegistration_DecoratesItAndKeepsLifetime()
    {
        var (assembly, interfaceType, implementationType) = await CompileQuickStartAsync("QuickStartApp_ExistingScoped");

        var services = new ServiceCollection();
        services.AddScoped(interfaceType, implementationType);
        InvokeStartup(assembly, "WithoutConfiguration", services);

        var registration = Assert.Single(services, d => d.ServiceType == interfaceType && !d.IsKeyedService);
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var firstScope = provider.CreateAsyncScope();
        await using var secondScope = provider.CreateAsyncScope();

        var first = firstScope.ServiceProvider.GetRequiredService(interfaceType);
        Assert.EndsWith("Decorator", first.GetType().Name);
        Assert.Same(first, firstScope.ServiceProvider.GetRequiredService(interfaceType));
        Assert.NotSame(first, secondScope.ServiceProvider.GetRequiredService(interfaceType));

        // The cache is shared across scopes, so the second scope's implementation is never called
        Assert.Equal("user-7", await GetUserAsync(interfaceType, first, 7));
        var second = secondScope.ServiceProvider.GetRequiredService(interfaceType);
        Assert.Equal("user-7", await GetUserAsync(interfaceType, second, 7));
    }

    [Fact]
    public async Task AddMethodCache_WithFactoryRegistration_DecoratesTheFactoryResult()
    {
        var (assembly, interfaceType, implementationType) = await CompileQuickStartAsync("QuickStartApp_ExistingFactory");
        var implementation = Activator.CreateInstance(implementationType)!;

        var services = new ServiceCollection();
        services.AddSingleton(interfaceType, _ => implementation);
        InvokeStartup(assembly, "WithoutConfiguration", services);

        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService(interfaceType);

        Assert.EndsWith("Decorator", service.GetType().Name);
        await GetUserAsync(interfaceType, service, 1);
        await GetUserAsync(interfaceType, service, 1);
        Assert.Equal(1, (int)implementationType.GetProperty("Calls")!.GetValue(implementation)!);
    }

    [Fact]
    public async Task AddMethodCache_CalledTwice_DoesNotWrapTheDecoratorAgain()
    {
        var (assembly, interfaceType, implementationType) = await CompileQuickStartAsync("QuickStartApp_CalledTwice");

        var services = new ServiceCollection();
        services.AddSingleton(interfaceType, implementationType);
        InvokeStartup(assembly, "WithoutConfiguration", services);
        InvokeStartup(assembly, "WithoutConfiguration", services);

        Assert.Single(services, d => d.ServiceType == interfaceType && !d.IsKeyedService);
        Assert.Single(services, d => d.ServiceType == interfaceType && d.IsKeyedService);
    }

    [Fact]
    public async Task AddMethodCache_WithDisposableImplementation_LetsTheContainerDisposeIt()
    {
        const string source = @"
using System;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace QuickStart
{
    public interface IRepository
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetAsync(int id);
    }

    public class Repository : IRepository, IDisposable
    {
        public static int Disposed;

        public Task<string> GetAsync(int id) => Task.FromResult(""item"");

        public void Dispose() => Disposed++;
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddScoped<IRepository, Repository>();
            services.AddMethodCache();
        }
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(source, "QuickStartApp_Disposable");
        var assembly = testAssembly.Assembly;
        var interfaceType = assembly.GetType("QuickStart.IRepository")!;
        var disposed = assembly.GetType("QuickStart.Repository")!.GetField("Disposed")!;

        var services = new ServiceCollection();
        InvokeStartup(assembly, "Configure", services);

        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService(interfaceType);
        }

        Assert.Equal(1, (int)disposed.GetValue(null)!);
    }

    [Fact]
    public async Task GeneratedRegistry_FindsDecoratorBeforeAnyCodeInTheAssemblyRuns()
    {
        var (_, interfaceType, implementationType) = await CompileQuickStartAsync("QuickStartApp_Registry");

        // Nothing in the compiled assembly has run yet; TryGet runs its module initializer
        Assert.True(MethodCacheDecorators.TryGet(interfaceType, out var registration));
        Assert.Equal(interfaceType, registration.ServiceType);
        Assert.Equal(implementationType, registration.ImplementationType);
        Assert.NotNull(registration.AddPolicies);

        Assert.False(MethodCacheDecorators.TryGet(typeof(IDisposable), out _));
    }

    private async Task<(Assembly Assembly, Type InterfaceType, Type ImplementationType)> CompileQuickStartAsync(string assemblyName)
    {
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(QuickStartSource, assemblyName);
        var assembly = testAssembly.Assembly;
        return (assembly, assembly.GetType("QuickStart.IUserService")!, assembly.GetType("QuickStart.UserService")!);
    }

    private static void InvokeStartup(Assembly assembly, string method, IServiceCollection services) =>
        assembly.GetType("QuickStart.Startup")!.GetMethod(method)!.Invoke(null, new object[] { services });

    private static Task<string> GetUserAsync(Type interfaceType, object service, int userId) =>
        (Task<string>)interfaceType.GetMethod("GetUserAsync")!.Invoke(service, new object[] { userId })!;
}
