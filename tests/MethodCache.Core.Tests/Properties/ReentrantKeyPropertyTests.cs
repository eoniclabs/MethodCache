using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class ReentrantKeyPropertyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantProvider_DoesNotOverwriteEarlierArguments(bool nestedThrows)
    {
        var generator = new FastHashKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("Outer");
        var provider = new ReentrantProvider(generator, policy, nestedThrows);
        Gen.Select(Gen.Int, Gen.Int).Sample((a, b) =>
        {
            if (a == b) return;
            Assert.NotEqual(generator.GenerateKey("Outer", [a, provider], policy),
                generator.GenerateKey("Outer", [b, provider], policy));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    [Fact]
    public void ReentrantJsonGetter_DoesNotOverwriteEarlierArguments()
    {
        var generator = new FastHashKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("Outer");
        var dto = new ReentrantDto(generator, policy);
        Assert.NotEqual(generator.GenerateKey("Outer", [1, dto], policy), generator.GenerateKey("Outer", [2, dto], policy));
    }

    private sealed class ReentrantProvider(ICacheKeyGenerator generator, CacheRuntimePolicy policy, bool nestedThrows) : ICacheKeyProvider
    {
        public string CacheKeyPart
        {
            get
            {
                try { generator.GenerateKey("Nested", [nestedThrows ? new ThrowingProvider() : 42], policy); }
                catch (InvalidOperationException) when (nestedThrows) { }
                return "part";
            }
        }
    }

    private sealed class ThrowingProvider : ICacheKeyProvider
    {
        public string CacheKeyPart => throw new InvalidOperationException("nested probe");
    }

    private sealed class ReentrantDto(ICacheKeyGenerator generator, CacheRuntimePolicy policy)
    {
        public int Id { get { generator.GenerateKey("Nested", [42], policy); return 1; } }
    }
}
