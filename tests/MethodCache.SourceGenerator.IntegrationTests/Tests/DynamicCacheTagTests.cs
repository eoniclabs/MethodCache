using Xunit;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// Verifies that tags with parameter templates such as "user:{userId}" are stored with the call's arguments,
/// so [CacheInvalidate] with the same template removes exactly the matching entries.
/// </summary>
public class DynamicCacheTagTests
{
    private const string Source = @"
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicTags
{
    public interface IUserService
    {
        [Cache(Duration = ""00:10:00"", Tags = new[] { ""user:{userId}"" })]
        Task<string> GetUserAsync(int userId);

        [Cache(Duration = ""00:10:00"", Tags = new[] { ""team:{Team}"" })]
        Task<string> GetTeamAsync(string? team);

        [CacheInvalidate(Tags = new[] { ""user:{userId}"" })]
        Task UpdateUserAsync(int userId);

        [CacheInvalidate(Tags = new[] { ""team:{team}"" })]
        Task UpdateTeamAsync(string? team);
    }

    public class UserService : IUserService
    {
        public Dictionary<string, int> Calls { get; } = new();

        public Task<string> GetUserAsync(int userId) => Task.FromResult(Count(""user:"" + userId));

        public Task<string> GetTeamAsync(string? team) => Task.FromResult(Count(""team:"" + (team ?? ""null"")));

        public Task UpdateUserAsync(int userId) => Task.CompletedTask;

        public Task UpdateTeamAsync(string? team) => Task.CompletedTask;

        private string Count(string key)
        {
            Calls[key] = Calls.TryGetValue(key, out var calls) ? calls + 1 : 1;
            return key;
        }
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services)
        {
            services.AddSingleton<UserService>();
            services.AddSingleton<IUserService>(sp => sp.GetRequiredService<UserService>());
            services.AddMethodCache();
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Fact]
    public async Task InvalidateWithTemplate_RemovesOnlyTheEntryForThatArgument()
    {
        var (service, calls) = await CreateServiceAsync("DynamicTags_ValueType");

        await service.Call("GetUserAsync", 42);
        await service.Call("GetUserAsync", 7);
        await service.Call("UpdateUserAsync", 42);
        await service.Call("GetUserAsync", 42);
        await service.Call("GetUserAsync", 7);

        Assert.Equal(2, calls()["user:42"]);
        Assert.Equal(1, calls()["user:7"]);
    }

    [Fact]
    public async Task InvalidateWithTemplate_MatchesNullArgumentsAndTemplatesInAnyCase()
    {
        var (service, calls) = await CreateServiceAsync("DynamicTags_Nullable");

        await service.Call("GetTeamAsync", (object?)null);
        await service.Call("GetTeamAsync", "red");
        await service.Call("UpdateTeamAsync", (object?)null);
        await service.Call("GetTeamAsync", (object?)null);
        await service.Call("GetTeamAsync", "red");

        Assert.Equal(2, calls()["team:null"]);
        Assert.Equal(1, calls()["team:red"]);
    }

    private async Task<(ServiceProxy Service, Func<Dictionary<string, int>> Calls)> CreateServiceAsync(string assemblyName)
    {
        var testAssembly = await _engine.CompileWithSourceGeneratorAsync(Source, assemblyName);
        var assembly = testAssembly.Assembly;

        var services = new ServiceCollection();
        assembly.GetType("DynamicTags.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services });
        var provider = services.BuildServiceProvider();

        var interfaceType = assembly.GetType("DynamicTags.IUserService")!;
        var implementationType = assembly.GetType("DynamicTags.UserService")!;
        var service = provider.GetRequiredService(interfaceType);
        Assert.EndsWith("Decorator", service.GetType().Name);

        var implementation = provider.GetRequiredService(implementationType);
        return (
            new ServiceProxy(interfaceType, service),
            () => (Dictionary<string, int>)implementationType.GetProperty("Calls")!.GetValue(implementation)!);
    }

    private sealed record ServiceProxy(Type InterfaceType, object Service)
    {
        public Task Call(string method, object? argument) =>
            (Task)InterfaceType.GetMethod(method)!.Invoke(Service, new[] { argument })!;
    }
}
