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

    /// <summary>
    /// Hidden state inside an argument (a property, an element of an array, list or dictionary, or a value behind
    /// an object-typed member) is refused as well: JSON would write it as <c>{}</c> and different values would share
    /// a key.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedPrivateState_IsNeverAcceptedAsAnEmptyKey(bool json)
    {
        ICacheKeyGenerator generator = json ? new JsonKeyGenerator() : new FastHashKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("Hidden");
        var shapes = new Func<int, object>[]
        {
            v => new Wrapper(new OwnPrivate(v)),
            v => new Wrapper2(new Derived(v)),
            v => new[] { new OwnPrivate(v) },
            v => new List<OwnPrivate> { new(v) },
            v => new Dictionary<string, OwnPrivate> { ["a"] = new(v) },
            v => new object[] { new FurtherDerived(v) },
            v => new Holder { Value = new Derived(v) },
            v => new Wrapper(new OwnPrivate(v)) with { },
        };

        Gen.Int.Select(Gen.Int[0, shapes.Length - 1]).Sample((value, shape) =>
        {
            var arg = shapes[shape](value);
            Assert.Throws<InvalidOperationException>(() => generator.GenerateKey("Hidden", [arg], policy));
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    /// <summary>Public state next to an empty, stateless type still makes a key.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatelessNestedType_IsAccepted(bool json)
    {
        ICacheKeyGenerator generator = json ? new JsonKeyGenerator() : new FastHashKeyGenerator();
        var policy = CacheRuntimePolicy.Empty("Hidden");
        Gen.Int.Sample(value =>
        {
            var a = generator.GenerateKey("Hidden", [new WithMarker(value, new Marker())], policy);
            var b = generator.GenerateKey("Hidden", [new WithMarker(value + 1, new Marker())], policy);
            Assert.NotEqual(a, b);
        }, seed: PropertySettings.Seed, iter: PropertySettings.PureIterations);
    }

    private sealed class OwnPrivate(int value)
    {
        private readonly int _value = value;
        public int Read() => _value;
    }

    private sealed record Wrapper(OwnPrivate Inner);

    private sealed record Wrapper2(Base Inner);

    private sealed class Holder
    {
        public object? Value { get; init; }
    }

    private sealed class Marker;

    private sealed record WithMarker(int Id, Marker Marker);

    private class Base(int value)
    {
        private readonly int _value = value;
        public int Read() => _value;
    }

    private sealed class Derived(int value) : Base(value);
    private class Middle(int value) : Base(value);
    private sealed class FurtherDerived(int value) : Middle(value);
}
