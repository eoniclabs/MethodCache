using MethodCache.Extensibility;
using MethodCache.Serialization.MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MethodCache.Core.Tests.Contracts;

public class ReviewFixContractTests
{
    [Theory]
    [InlineData(typeof(FastHashKeyGenerator))]
    [InlineData(typeof(JsonKeyGenerator))]
    [InlineData(typeof(MessagePackKeyGenerator))]
    public void CollectionScope_GeneratedDecorator_KeepsCallersApart(Type generatorType)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IScopeReader, ScopeReader>();
        services.AddMethodCache();
        services.Replace(ServiceDescriptor.Singleton(typeof(ICacheKeyGenerator), generatorType));
        using var provider = services.BuildServiceProvider();
        var reader = provider.GetRequiredService<IScopeReader>();
        var alice = new ScopedList("alice") { 1, 2 };
        var bob = new ScopedList("bob") { 1, 2 };
        Assert.Equal("alice", reader.Read(alice));
        Assert.Equal("bob", reader.Read(bob));
        Assert.Equal("alice", reader.Read(alice));
    }

    [Fact]
    public async Task BulkRead_InvalidatedWhileLoading_IsNotPublished()
    {
        var services = new ServiceCollection();
        services.AddMethodCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var pending = new TaskCompletionSource<IDictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = cache.GetOrCreateManyAsync<string>(["key"], (_, _) =>
        {
            started.SetResult();
            return new(pending.Task);
        }, o => o.WithTags("scope"));
        await started.Task;
        await cache.InvalidateByTagsAsync("scope");
        pending.SetResult(new Dictionary<string, string> { ["key"] = "old" });
        Assert.Equal("old", (await batch)["key"]);
        Assert.False((await cache.TryGetAsync<string>("key")).Found);
    }

    public interface IScopeReader { [Cache(Tags = new[] { "scopes" })] string Read(ScopedList values); }
    public sealed class ScopeReader : IScopeReader { public string Read(ScopedList values) => values.Scope; }
    public sealed class ScopedList(string scope) : List<int>
    {
        public string Scope { get; } = scope;
        public override string ToString() => throw new InvalidOperationException("Static tags must not format this argument.");
    }
}
