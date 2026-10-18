using System.Text.Json.Serialization;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class PolymorphicContextTests
{
    [Fact]
    public async Task ConfiguredContext_PreservesNestedDerivedAndPrivateSetterState()
    {
        var time = new FakeTimeProvider();
        var shared = new MethodCache.TestSupport.AtomicTestCache(time);
        ServiceProvider Node()
        {
            var services = new ServiceCollection();
            services.AddSingleton<TimeProvider>(time);
            services.AddMethodCache(builder => builder.UseJson(PolymorphicTestContext.Default));
            services.AddMethodCacheDistributedCache(_ => shared);
            return services.BuildServiceProvider();
        }
        using var a = Node();
        using var b = Node();
        var value = new ContextPayload([new ContextDog { Name = "rex", Breed = "collie" }], new PrivateState(42));
        await a.GetRequiredService<ICacheManager>().GetOrCreateAsync("context", (_, _) => ValueTask.FromResult(value));
        var result = await b.GetRequiredService<ICacheManager>().GetOrCreateAsync<ContextPayload>("context", (_, _) => throw new InvalidOperationException("Expected L2"));
        Assert.Equal("collie", Assert.IsType<ContextDog>(Assert.Single(result.Animals)).Breed);
        Assert.Equal(42, result.State.Secret);
    }

    [Fact]
    public async Task DistributedFalse_PerformsNoSerializerOrBackendOperations()
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddMethodCache();
        var backend = new ThrowingCache();
        services.AddMethodCacheDistributedCache(_ => backend);
        var serializer = new ThrowingSerializer();
        services.AddKeyedSingleton<ISerializer>(OwnedServices.Key, serializer);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var options = new CacheEntryOptions.Builder().WithDistributed(false).Build();
        Assert.Equal(7, await cache.GetOrCreateAsync("local", (_, _) => ValueTask.FromResult(7), options));
        Assert.Equal(7, await cache.GetOrCreateAsync<int>("local", (_, _) => throw new InvalidOperationException("Expected L1"), options));
        Assert.Equal(0, backend.Operations);
        Assert.Equal(0, serializer.Operations);
    }

    private sealed class ThrowingSerializer : ISerializer
    {
        public int Operations;
        public string ContentType => "application/test";
        private InvalidOperationException Unexpected() { Operations++; return new("No serialization"); }
        public T? Deserialize<T>(System.Buffers.ReadOnlySequence<byte> source) => throw Unexpected();
        public void Serialize<T>(T value, System.Buffers.IBufferWriter<byte> destination) => throw Unexpected();
    }

    private sealed class ThrowingCache : IAtomicInvalidationStore
    {
        public int Operations;
        private InvalidOperationException Unexpected() { Operations++; return new("No backend operations"); }
        public byte[]? Get(string key) => throw Unexpected();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Unexpected();
        public void Set(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options) => throw Unexpected();
        public Task SetAsync(string key, byte[] value, Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions options, CancellationToken token = default) => throw Unexpected();
        public void Refresh(string key) => throw Unexpected();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Unexpected();
        public void Remove(string key) => throw Unexpected();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Unexpected();
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default) => throw Unexpected();
    }
}

[JsonDerivedType(typeof(ContextDog), "dog")]
public class ContextAnimal
{
    public string Name { get; set; } = "";
}
public sealed class ContextDog : ContextAnimal
{
    public string Breed { get; set; } = "";
}
public sealed class PrivateState
{
    [JsonConstructor]
    public PrivateState(int secret) => Secret = secret;
    public int Secret { get; private set; }
}
public sealed record ContextPayload(List<ContextAnimal> Animals, PrivateState State);

[JsonSerializable(typeof(ContextPayload))]
internal partial class PolymorphicTestContext : JsonSerializerContext;
