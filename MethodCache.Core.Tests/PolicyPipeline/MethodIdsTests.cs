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

    public interface IOrders
    {
    }

    public interface IRepository<TKey, TValue>
    {
    }
}
