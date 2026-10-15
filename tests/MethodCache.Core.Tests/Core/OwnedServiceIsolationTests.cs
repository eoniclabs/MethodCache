using System.Buffers;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class OwnedServiceIsolationTests
{
    [Fact]
    public void ApplicationCache_IsNeverAStorageFallback()
    {
        var applicationCache = new MethodCache.TestSupport.AtomicTestCache();
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(applicationCache);
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache();
        using var provider = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ICacheManager>());
        Assert.Same(applicationCache, provider.GetRequiredService<IDistributedCache>());
    }

    [Fact]
    public void SerializerSelection_LeavesApplicationRegistrationsAlone_AndOwnsOptions()
    {
        var applicationSerializer = new ApplicationSerializer();
        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = false };
        var services = new ServiceCollection();
        services.AddSingleton<ISerializer>(applicationSerializer);
        services.AddMethodCache(builder => builder.UseJson(options));
        options.WriteIndented = true;
        using var provider = services.BuildServiceProvider();
        Assert.Same(applicationSerializer, provider.GetRequiredService<ISerializer>());
        var owned = provider.GetRequiredKeyedService<ISerializer>(OwnedServices.Key);
        var buffer = new ArrayBufferWriter<byte>();
        owned.Serialize(new Payload(1), buffer);
        Assert.DoesNotContain("\n", System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public sealed record Payload(int Value);
    private sealed class ApplicationSerializer : ISerializer
    {
        public string ContentType => "application/test";
        public T? Deserialize<T>(ReadOnlySequence<byte> source) => throw new InvalidOperationException("Application serializer must not be used.");
        public void Serialize<T>(T value, IBufferWriter<byte> destination) => throw new InvalidOperationException("Application serializer must not be used.");
    }
}
