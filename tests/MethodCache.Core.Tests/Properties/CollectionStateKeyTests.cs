using CsCheck;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class CollectionStateKeyTests
{
    [Theory]
    [InlineData(typeof(FastHashKeyGenerator))]
    [InlineData(typeof(JsonKeyGenerator))]
    public void Collection_PublicStateAndElements_IdentifyTheArgument(Type generatorType)
    {
        var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
        var policy = CacheRuntimePolicy.Empty("collections");
        string Key(object value) => generator.GenerateKey("collections", [value], policy);
        Gen.Int[0, 100].Sample(value =>
        {
            var a = new StatefulList { Scope = "a", Flags = value, State = new State(value) };
            a.AddRange([1, 2]);
            var b = new StatefulList { Scope = "b", Flags = value, State = new State(value) };
            b.AddRange([1, 2]);
            Assert.NotEqual(Key(a), Key(b));
            b.Scope = "a";
            Assert.Equal(Key(a), Key(b));
            b.Flags++;
            Assert.NotEqual(Key(a), Key(b));
            b.Flags--;
            b.State = new State(value + 1);
            Assert.NotEqual(Key(a), Key(b));
            b.State = a.State;
            b.Add(3);
            Assert.NotEqual(Key(a), Key(b));
            Assert.NotEqual(Key(new Holder(a)), Key(new Holder(b)));
            Assert.NotEqual(Key(new StatefulDictionary { Scope = "a", ["x"] = value }),
                Key(new StatefulDictionary { Scope = "b", ["x"] = value }));
        }, seed: PropertySettings.Seed, iter: 20);
    }

    public record State(int Value);
    public record Holder(List<int> Values);
    public sealed class StatefulList : List<int>
    {
        public string Scope { get; set; } = "";
        public int Flags;
        public State State { get; set; } = new(0);
    }
    public sealed class StatefulDictionary : Dictionary<string, int>
    {
        public string Scope { get; set; } = "";
    }
}
