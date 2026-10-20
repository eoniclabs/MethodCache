using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class GenericPolicyInvalidationTests
{
    [Fact]
    public async Task MethodOverride_RetiresEveryClosedAndKeyedRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddSingleton<IGeneric<int>>(new Numbers());
        services.AddKeyedSingleton<IGeneric<string>>("a", new Names());
        services.AddKeyedSingleton<IGeneric<string>>("b", new Names());
        services.AddMethodCache(b => b.FromAssemblyOf<GenericPolicyInvalidationTests>());
        await using var provider = services.BuildServiceProvider();
        var numbers = provider.GetRequiredService<IGeneric<int>>();
        var a = provider.GetRequiredKeyedService<IGeneric<string>>("a");
        var b = provider.GetRequiredKeyedService<IGeneric<string>>("b");
        Assert.Equal(1, await numbers.ReadAsync(1));
        Assert.Equal(1, await a.ReadAsync(1));
        Assert.Equal(1, await b.ReadAsync(1));
        await provider.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync<IGeneric<int>>(s => s.ReadAsync(1), o => o.WithDuration(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, await numbers.ReadAsync(1));
        Assert.Equal(2, await a.ReadAsync(1));
        Assert.Equal(2, await b.ReadAsync(1));
        Assert.Equal(3, provider.GetRequiredService<ICacheAdministrator>().ListEntries().Count);
        Assert.Single(provider.GetRequiredService<IRuntimeCacheConfigurator>().GetOverrides());
    }

    public interface IGeneric<T> { [Cache] Task<int> ReadAsync(int id); }
    public sealed class Numbers : IGeneric<int>
    {
        private int _calls;
        public Task<int> ReadAsync(int id) => Task.FromResult(++_calls);
    }
    public sealed class Names : IGeneric<string>
    {
        private int _calls;
        public Task<int> ReadAsync(int id) => Task.FromResult(++_calls);
    }
}
