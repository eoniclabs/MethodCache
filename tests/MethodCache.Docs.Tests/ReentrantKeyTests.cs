using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Docs.Tests;

public class ReentrantKeyTests
{
    [Fact]
    public void KeyProvider_CanComposeAnotherKeyWithoutLosingOuterArguments()
    {
        var generator = new FastHashKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("Outer");
        var part = new Part(generator, policy);
        Assert.NotEqual(generator.GenerateKey("Outer", [1, part], policy), generator.GenerateKey("Outer", [2, part], policy));
    }

    private sealed class Part(ICacheKeyGenerator generator, CacheRuntimePolicy policy) : ICacheKeyProvider
    {
        public string CacheKeyPart => generator.GenerateKey("Nested", [42], policy);
    }
}
