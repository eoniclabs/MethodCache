using System.Reflection;
using System.Reflection.Emit;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class AssemblyKeyIdentityTests
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

    private static Type Define(string assemblyName)
        => AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assemblyName), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("types").DefineType("Collision.IService", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract).CreateType()!;
}
