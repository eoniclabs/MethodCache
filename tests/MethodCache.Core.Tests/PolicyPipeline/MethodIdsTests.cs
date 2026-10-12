using MethodCache.Core.PolicyPipeline.Model;
using Xunit;

namespace MethodCache.Core.Tests.PolicyPipeline;

public class MethodIdsTests
{
    [Fact]
    public void For_NestedType_UsesDotsLikeTheGenerator()
    {
        Assert.Equal("MethodCache.Core.Tests.PolicyPipeline.MethodIdsTests.IOrders.Get", MethodIds.For(typeof(IOrders), "Get"));
    }

    [Fact]
    public void For_GenericType_NamesTheDefinitionLikeTheGenerator()
    {
        const string expected = "MethodCache.Core.Tests.PolicyPipeline.MethodIdsTests.IRepository<TKey, TValue>.Get";

        Assert.Equal(expected, MethodIds.For(typeof(IRepository<,>), "Get"));
        Assert.Equal(expected, MethodIds.For(typeof(IRepository<int, string>), "Get"));
    }

    [Fact]
    public void For_Method_NamesTheDeclaringInterfaceDefinition()
    {
        // Configured through a derived, closed interface, the id is still the declaring definition's
        var get = typeof(IUserRepository).GetInterface("IRepository`2")!.GetMethod("Get")!;

        Assert.Equal("MethodCache.Core.Tests.PolicyPipeline.MethodIdsTests.IRepository<TKey, TValue>.Get", MethodIds.For(get));
    }

    [Fact]
    public void For_OverloadedMethod_AddsParameterTypesLikeTheGenerator()
    {
        const string prefix = "MethodCache.Core.Tests.PolicyPipeline.MethodIdsTests.IOrders.";
        var finds = typeof(IOrders).GetMethods().Where(m => m.Name == "Find").Select(m => MethodIds.For(m).Substring(prefix.Length)).OrderBy(id => id, StringComparer.Ordinal).ToList();

        Assert.Equal(new[]
        {
            "Find(int)",
            "Find(int?, string[], System.Collections.Generic.List<System.Guid>, T)",
            "Find(string, System.Threading.CancellationToken)",
        }, finds);
        Assert.Equal(prefix + "Get", MethodIds.For(typeof(IOrders).GetMethod("Get")!));
    }

    public interface IOrders
    {
        Task<string> Get(int id);
        Task<string> Find(int id);
        Task<string> Find(string name, CancellationToken cancellationToken);
        Task<string> Find<T>(int? id, string[] names, List<Guid> ids, T extra);
    }

    public interface IRepository<TKey, TValue>
    {
        Task<TValue> Get(TKey key);
    }

    public interface IUserRepository : IRepository<int, string>
    {
    }
}
