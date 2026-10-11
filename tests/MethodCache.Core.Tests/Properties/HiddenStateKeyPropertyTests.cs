using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class HiddenStateKeyPropertyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritedPrivateState_IsNeverAcceptedAsAnEmptyKey(bool json)
    {
        ICacheKeyGenerator generator = json ? new JsonKeyGenerator() : new FastHashKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("Hidden");
        Gen.Int.Sample(value =>
        {
            Assert.Throws<InvalidOperationException>(() => generator.GenerateKey("Hidden", [new Derived(value)], policy));
            Assert.Throws<InvalidOperationException>(() => generator.GenerateKey("Hidden", [new FurtherDerived(value)], policy));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    private class Base(int value)
    {
        private readonly int _value = value;
        public int Read() => _value;
    }

    private sealed class Derived(int value) : Base(value);
    private class Middle(int value) : Base(value);
    private sealed class FurtherDerived(int value) : Middle(value);
}
