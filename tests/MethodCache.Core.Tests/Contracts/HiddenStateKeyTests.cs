using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Contracts;

public class HiddenStateKeyTests
{
    [Fact]
    public void StateInheritedFromPrivateBaseFields_IsRefusedByBothGenerators()
    {
        ICacheKeyGenerator[] generators = [new FastHashKeyGenerator(), new JsonKeyGenerator()];
        foreach (var generator in generators)
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                generator.GenerateKey("Hidden", [new Derived(42)], CacheRuntimePolicy.Empty("Hidden")));
            Assert.Contains("state is not public", error.Message);
        }
    }

    private class Base(int value)
    {
        private readonly int _value = value;
        public int Read() => _value;
    }

    private sealed class Derived(int value) : Base(value);
}
