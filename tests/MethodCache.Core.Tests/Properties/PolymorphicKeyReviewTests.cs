using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Serialization.MessagePack;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class PolymorphicKeyReviewTests
{
    [Theory]
    [InlineData(typeof(FastHashKeyGenerator))]
    [InlineData(typeof(JsonKeyGenerator))]
    [InlineData(typeof(MessagePackKeyGenerator))]
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

    [Fact]
    public void MessagePack_NestedSlots_PreserveRuntimeTypesAndNulls()
    {
        var keys = new MessagePackKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("slots");
        string Key(object value) => keys.GenerateKey("slots", [value], policy);

        Assert.NotEqual(Key(new Holder { Inner = null }), Key(new Holder { Inner = new Base { Id = 1 } }));
        Assert.NotEqual(Key(new Holder { Inner = new Base { Id = 1 } }), Key(new Holder { Inner = new Base { Id = 2 } }));
        Assert.NotEqual(Key(new InterfaceHolder { Inner = new Derived { Value = 1 } }), Key(new InterfaceHolder { Inner = new Derived { Value = 2 } }));
        Assert.NotEqual(Key(new Base[] { new Derived { Value = 1 } }), Key(new Base[] { new Derived { Value = 2 } }));
        Assert.NotEqual(Key(new List<Base> { new Derived { Value = 1 } }), Key(new List<Base> { new Derived { Value = 2 } }));
        Assert.Equal(Key(new Holder { Inner = new Derived { Id = 1, Value = 2 } }), Key(new Holder { Inner = new Derived { Id = 1, Value = 2 } }));
    }

    public interface IBase { int Id { get; set; } }
    public class Base : IBase { public int Id { get; set; } }
    public sealed class Derived : Base { public int Value { get; set; } }
    public sealed class Holder { public Base? Inner { get; set; } }
    public sealed class InterfaceHolder { public IBase? Inner { get; set; } }
}
