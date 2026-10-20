using MethodCache.TestSupport;
using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class AssemblyKeySecurityTests
{
    private interface IGeneric<T> { }

    [Fact]
    public void Prefix_DistinguishesAssembliesAndGenericArgumentAssemblies()
    {
        var left = Define("Microsoft.CacheIdentityLeft");
        var right = Define("Microsoft.CacheIdentityRight");
        Assert.Equal(left.ToString(), right.ToString());
        Assert.NotEqual(global::MethodCache.Core.PolicyPipeline.Model.MethodIds.For(left, "Read"),
            global::MethodCache.Core.PolicyPipeline.Model.MethodIds.For(right, "Read"));
        Assert.NotEqual(MethodCacheKeys.Prefix(left, "Read"), MethodCacheKeys.Prefix(right, "Read"));
        Assert.NotEqual(MethodCacheKeys.Prefix(typeof(IGeneric<>).MakeGenericType(left), "Read"),
            MethodCacheKeys.Prefix(typeof(IGeneric<>).MakeGenericType(right), "Read"));
    }

    private sealed record Envelope(object Value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackArguments_KeepSameNamedRuntimeTypesInDifferentAssembliesSeparate(bool nested)
    {
        static object Value(string assembly)
        {
            var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assembly), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("values").DefineType("Collision.Value", TypeAttributes.Public | TypeAttributes.Sealed);
            builder.DefineField("Id", typeof(int), FieldAttributes.Public);
            var type = builder.CreateType()!;
            var value = Activator.CreateInstance(type)!;
            type.GetField("Id")!.SetValue(value, 42);
            return value;
        }
        object left = Value("PayloadLeft");
        object right = Value("PayloadRight");
        if (nested) { left = new Envelope(left); right = new Envelope(right); }
        var policy = global::MethodCache.Infrastructure.CacheRuntimePolicy.Empty("scope.Read");
        foreach (var generator in new global::MethodCache.Extensibility.ICacheKeyGenerator[]
            { new global::MethodCache.Extensibility.FastHashKeyGenerator(), new global::MethodCache.Extensibility.JsonKeyGenerator() })
            Assert.NotEqual(generator.GenerateKey("scope.Read", [left], policy), generator.GenerateKey("scope.Read", [right], policy));
    }

    [Fact]
    public void KeyedRegistrations_KeepSameNamedEnumTypesInDifferentAssembliesSeparate()
    {
        static object Key(string assembly)
        {
            var builder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assembly), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("keys").DefineEnum("Collision.Region", TypeAttributes.Public, typeof(int));
            builder.DefineLiteral("Eu", 1);
            return Enum.ToObject(builder.CreateTypeInfo()!.AsType(), 1);
        }
        Assert.NotEqual(TestKeys.KeyedMethodName("Read", Key("KeyLeft")),
            TestKeys.KeyedMethodName("Read", Key("KeyRight")));
    }

    private static Type Define(string assemblyName)
        => AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("types").DefineType("Collision.IService", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType()!;
}
