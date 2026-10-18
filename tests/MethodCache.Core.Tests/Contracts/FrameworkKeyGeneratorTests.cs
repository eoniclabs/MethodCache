using Microsoft.AspNetCore.Http;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Serialization.MessagePack;
using Xunit;
namespace MethodCache.Core.Tests.Contracts;

public class FrameworkKeyGeneratorTests
{
    [Theory]
    [InlineData(nameof(FastHashKeyGenerator))]
    [InlineData(nameof(JsonKeyGenerator))]
    [InlineData(nameof(MessagePackKeyGenerator))]
    public void AspNetCoreRequestValues_GetDifferentKeysInEveryKeyGenerator(string name)
    {
        ICacheKeyGenerator generator = name switch
        {
            nameof(FastHashKeyGenerator) => new FastHashKeyGenerator(),
            nameof(JsonKeyGenerator) => new JsonKeyGenerator(),
            _ => new MessagePackKeyGenerator(),
        };
        string Key(object value) => generator.GenerateKey("Requests", [value], CacheRuntimePolicy.Empty("Requests"));

        Assert.NotEqual(Key(new PathString("/a")), Key(new PathString("/b")));
        Assert.Equal(Key(new PathString("/a")), Key(new PathString("/a")));
        Assert.NotEqual(Key(new QueryString("?x=1")), Key(new QueryString("?x=2")));
        Assert.NotEqual(Key(new HostString("example.com", 80)), Key(new HostString("example.com", 81)));
    }

}
