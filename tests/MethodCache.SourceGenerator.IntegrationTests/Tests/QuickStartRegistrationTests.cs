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
                config.FromAssemblyOf<IUserService>().AutoRegister(ServiceLifetime.Singleton);
                config.Defaults(policy => policy.WithDuration(TimeSpan.FromMinutes(10)));
            });
        }

        public static void WithoutConfiguration(IServiceCollection services)
        {
            services.AddMethodCache();
        }

        public static void WithAutoRegister(IServiceCollection services)
        {
            services.AddMethodCache(b => b.FromAssemblyOf<IUserService>().AutoRegister(ServiceLifetime.Singleton));
        }

        public static void WithExplicitAssembly(IServiceCollection services)
        {
            services.AddMethodCache(b => b.FromAssemblyOf<IUserService>().AutoRegister(ServiceLifetime.Singleton));
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task AddMethodCache_NoAutoRegister_DoesNotChooseImplementation()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(QuickStartSource, "ExplicitAutoRegistrationContract");
        var services = new ServiceCollection();
        compiled.Assembly.GetType("QuickStart.Startup")!.GetMethod("WithoutConfiguration")!.Invoke(null, [services]);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == compiled.Assembly.GetType("QuickStart.IUserService"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task AutoRegister_ZeroOrMultipleCandidates_ReportsNamedError(int candidates)
    {
        var source = candidates == 0
            ? QuickStartSource.Replace("public class UserService : IUserService", "public class UserService")
            : QuickStartSource.Replace("public static class Startup", "public class OtherUserService : UserService { }\n    public static class Startup");
        var compiled = await _engine.CompileWithSourceGeneratorAsync(
            source.Replace("ServiceLifetime.Singleton", "ServiceLifetime.Scoped"), "AutoCandidates" + candidates);
        var invocation = Assert.Throws<TargetInvocationException>(() => InvokeStartup(compiled.Assembly, "WithExplicitAssembly", new ServiceCollection()));
        var error = Assert.IsType<InvalidOperationException>(invocation.InnerException);
        Assert.Contains("IUserService", error.Message);
        Assert.Contains($"found {candidates}", error.Message);
        Assert.Contains("AutoCandidates" + candidates, error.Message);
    }

    [Fact]
    public async Task AutoRegister_OneCandidate_PreservesExplicitScopedLifetime()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(
            QuickStartSource.Replace("ServiceLifetime.Singleton", "ServiceLifetime.Scoped"), "ScopedAutoCandidate");
        var services = new ServiceCollection();
        InvokeStartup(compiled.Assembly, "WithExplicitAssembly", services);
        var type = compiled.Assembly.GetType("QuickStart.IUserService")!;
        Assert.Equal(ServiceLifetime.Scoped, services.Last(d => d.ServiceType == type && !d.IsKeyedService).Lifetime);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        Assert.Same(first.ServiceProvider.GetRequiredService(type), first.ServiceProvider.GetRequiredService(type));
        Assert.NotSame(first.ServiceProvider.GetRequiredService(type), second.ServiceProvider.GetRequiredService(type));
    }

    [Fact]
    public async Task AutoRegister_MultipleCandidates_PreservesExplicitInstance()
    {
        var source = QuickStartSource.Replace("public static class Startup",
            "public class OtherUserService : UserService { }\n    public static class Startup");
        var compiled = await _engine.CompileWithSourceGeneratorAsync(source, "ExplicitInstanceWithTwoCandidates");
        var type = compiled.Assembly.GetType("QuickStart.IUserService")!;
        var inner = Activator.CreateInstance(compiled.Assembly.GetType("QuickStart.OtherUserService")!)!;
        var services = new ServiceCollection();
        services.AddSingleton(type, inner);
        InvokeStartup(compiled.Assembly, "WithAutoRegister", services);
        await using var provider = services.BuildServiceProvider();
        var decorator = provider.GetRequiredService(type);
        Assert.Same(inner, decorator.GetType().GetField("_decorated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decorator));
        var get = type.GetMethod("GetUserAsync")!;
        Assert.Equal("user-7", await (Task<string>)get.Invoke(decorator, [7])!);
        Assert.Equal("user-7", await (Task<string>)get.Invoke(decorator, [7])!);
        Assert.Equal(1, inner.GetType().GetProperty("Calls")!.GetValue(inner));
    }

    [Fact]
    public async Task Binding_CacheableRegistrationAddedAfterStartup_ReportsOrderError()
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(QuickStartSource, "LateCacheRegistration");
        var type = compiled.Assembly.GetType("QuickStart.IUserService")!;
        var implementation = compiled.Assembly.GetType("QuickStart.UserService")!;
        var services = new ServiceCollection();
        services.AddSingleton(type, implementation);
        services.AddMethodCache();
        services.AddSingleton("late-non-cacheable");
        await using (var valid = services.BuildServiceProvider())
        {
            Assert.Equal("late-non-cacheable", valid.GetRequiredService<string>());
            Assert.Contains("Decorator", valid.GetRequiredService(type).GetType().Name);
        }
        services.AddSingleton(type, implementation);
        await using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetServices(type).ToArray());
        Assert.Contains("registered after AddMethodCache", error.Message);
    }

    [Theory]
    [InlineData("Singleton", ServiceLifetime.Singleton)]
    [InlineData("Scoped", ServiceLifetime.Scoped)]
    [InlineData("Transient", ServiceLifetime.Transient)]
    public async Task FactoryRegistration_ExplicitLifetime_IsPreserved(string suffix, ServiceLifetime lifetime)
    {
        var source = QuickStartSource.Replace(
            "public static class Startup",
            $$"""
            public static class ExplicitRegistration
            {
                public static void Register(IServiceCollection services)
                {
                    services.Add{{suffix}}<IUserService>(_ => new UserService());
                    services.AddMethodCache();
                }
            }
            public static class Startup
            """);
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(source, $"ExplicitRegistration_{suffix}");
        var assembly = testAssembly.Assembly;
        var interfaceType = assembly.GetType("QuickStart.IUserService")!;
        var services = new ServiceCollection();
        assembly.GetType("QuickStart.ExplicitRegistration")!.GetMethod("Register")!.Invoke(null, new object[] { services });
        Assert.Equal(lifetime, services.Last(d => d.ServiceType == interfaceType).Lifetime);

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService(interfaceType);
        var repeated = firstScope.ServiceProvider.GetRequiredService(interfaceType);
        var otherScope = secondScope.ServiceProvider.GetRequiredService(interfaceType);
        Assert.Equal(lifetime != ServiceLifetime.Transient, ReferenceEquals(first, repeated));
        Assert.Equal(lifetime == ServiceLifetime.Singleton, ReferenceEquals(first, otherScope));

        Assert.Null(assembly.GetType("Microsoft.Extensions.DependencyInjection.GeneratedCachingServiceCollectionExtensions"));
        var get = interfaceType.GetMethod("GetUserAsync")!;
        Assert.Equal("user-7", await (Task<string>)get.Invoke(first, new object[] { 7 })!);
        Assert.Equal("user-7", await (Task<string>)get.Invoke(first, new object[] { 7 })!);
    }

    [Theory]
    [InlineData("WithConfiguration")]
    [InlineData("WithAutoRegister")]
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

        var implementation = service.GetType().GetField("_decorated", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
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

namespace QuickStart
{
    public interface IExternalService
    {
        [Cache(Duration = ""00:10:00"")]
        Task<string> GetAsync(int id);
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services) => services.AddMethodCache(b => b.FromAssembly(typeof(Startup).Assembly));
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
    public async Task AddMethodCache_WithSeveralCachedInterfaces_RegistersOnePolicySourcePerBoundService()
    {
        const string source = @"
using System.Threading.Tasks;
using MethodCache;
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
        public static void Configure(IServiceCollection services) => services.AddMethodCache(b => b.FromAssembly(typeof(Startup).Assembly).AutoRegister(ServiceLifetime.Singleton));
    }
}";

        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(source, "QuickStartApp_SeveralInterfaces");
        var assembly = testAssembly.Assembly;

        var services = new ServiceCollection();
        assembly.GetType("QuickStart.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });

        await using var provider = services.BuildServiceProvider();
        var generatedSources = provider.GetServices<PolicySourceRegistration>()
            .Where(r => r.Source.SourceId == "attributes");
        Assert.Equal(2, generatedSources.Count());
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

        var services = new ServiceCollection();
        services.AddSingleton(interfaceType, implementationType);
        services.AddMethodCache(b => b.FromAssembly(interfaceType.Assembly));
        await using var provider = services.BuildServiceProvider();
        Assert.EndsWith("Decorator", provider.GetRequiredService(interfaceType).GetType().Name);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IDisposable));
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
