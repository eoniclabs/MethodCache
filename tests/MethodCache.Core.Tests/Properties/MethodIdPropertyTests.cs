using CsCheck;
using MethodCache.Core.PolicyPipeline.Model;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class MethodIdPropertyTests
{
    [Fact]
    public void DifferentOverloadParameterTypes_AlwaysHaveDifferentIds()
    {
        var methods = typeof(IOverloads).GetMethods();
        Gen.Select(Gen.Int[0, methods.Length - 1], Gen.Int[0, methods.Length - 1]).Sample((a, b) =>
        {
            if (a != b) Assert.NotEqual(MethodIds.For(methods[a]), MethodIds.For(methods[b]));
        }, iter: PropertySettings.PureIterations, seed: PropertySettings.Seed);
    }

    public interface IOverloads
    {
        string Find(First.Request value);
        string Find(Second.Request value);
        string Find(First.Request[] value);
        string Find(Second.Request[] value);
        string Find(List<First.Request> value);
        string Find(List<Second.Request> value);
        string Find(Outer<int>.Inner<string> value);
        string Find(Outer<long>.Inner<string> value);
    }

    public static class First { public sealed record Request(int Id); }
    public static class Second { public sealed record Request(int Id); }
    public class Outer<T> { public sealed record Inner<U>(T First, U Second); }
}
