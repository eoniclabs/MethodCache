using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Docs.Tests;

public class UnmanagedConstraintTests
{
    [Fact]
    public async Task UnmanagedInterfaceAndMethod_AreDecoratedAndCached()
    {
        var implementation = new Values();
        var services = new ServiceCollection();
        services.AddSingleton<IValues<int>>(implementation);
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var values = provider.GetRequiredService<IValues<int>>();
        Assert.Equal(42, values.Get(42));
        Assert.Equal(42, values.Get(42));
        Assert.Equal(7L, values.Convert(7L));
        Assert.Equal(7L, values.Convert(7L));
        Assert.Equal(2, implementation.Calls);
    }

    public interface IValues<T> where T : unmanaged
    {
        [Cache] T Get(int id);
        [Cache] U Convert<U>(U value) where U : unmanaged;
    }

    private sealed class Values : IValues<int>
    {
        public int Calls;
        public int Get(int id) { Calls++; return id; }
        public U Convert<U>(U value) where U : unmanaged { Calls++; return value; }
    }
}
