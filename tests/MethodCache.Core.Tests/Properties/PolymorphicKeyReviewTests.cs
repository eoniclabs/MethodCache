using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class PolymorphicKeyReviewTests
{
    [Theory]
    [InlineData(typeof(FastHashKeyGenerator))]
    [InlineData(typeof(JsonKeyGenerator))]
    public void NestedDerivedState_IdentifiesTheArgument(Type generatorType)
    {
        var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
        var policy = CacheRuntimePolicy.Empty("polymorphic");
        Gen.Int[0, 100].Sample(value =>
        {
            var a = new Holder { Inner = new Derived { Id = 1, Value = value } };
            var b = new Holder { Inner = new Derived { Id = 1, Value = value + 1 } };
            Assert.NotEqual(generator.GenerateKey("polymorphic", [a], policy), generator.GenerateKey("polymorphic", [b], policy));
        }, seed: PropertySettings.Seed, iter: 20);
    }

    public interface IBase { int Id { get; set; } }
    public class Base : IBase { public int Id { get; set; } }
    public sealed class Derived : Base { public int Value { get; set; } }
    public sealed class Holder { public Base? Inner { get; set; } }
    public sealed class InterfaceHolder { public IBase? Inner { get; set; } }
}
