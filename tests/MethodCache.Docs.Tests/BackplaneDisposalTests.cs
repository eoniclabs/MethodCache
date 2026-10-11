using MethodCache.Extensibility;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.Docs.Tests;

public class BackplaneDisposalTests
{
    [Fact]
    public async Task AsyncProviderDisposal_UnsubscribesFromApplicationSuppliedBackplane()
    {
        var backplane = new AppBackplane();
        var services = new ServiceCollection();
        services.AddSingleton<IBackplane>(backplane);
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICacheManager>();
        await provider.DisposeAsync();
        Assert.Equal(1, backplane.Unsubscriptions);
        Assert.False(backplane.Disposed);
    }

    private sealed class AppBackplane : IBackplane, IDisposable
    {
        public int Unsubscriptions;
        public bool Disposed;
        public string InstanceId => "app";
        public Task SubscribeAsync(Func<BackplaneMessage, Task> onMessage, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnsubscribeAsync(CancellationToken cancellationToken = default) { Unsubscriptions++; return Task.CompletedTask; }
        public Task PublishInvalidationAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTagInvalidationAsync(string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}
