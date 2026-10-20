using MethodCache.Core.PolicyPipeline.Model;
using Xunit;

namespace MethodCache.Core.Tests.PolicyPipeline;

public class TagTemplateRulesTests
{
    [Theory]
    [InlineData("users")]
    [InlineData("user:{userId}")]
    [InlineData("user:{USERID}:{tenant}")]
    [InlineData("literal {")]
    [InlineData("{ }")]
    public void Check_EveryPlaceholderNamesOneParameter_ReturnsNull(string tag)
        => Assert.Null(TagTemplateRules.Check(tag, ["userId", "tenant"]));

    [Theory]
    [InlineData("user:{id}", new[] { "userId" }, "{id} names no parameter of the method, whose parameters are userId", "MC0010")]
    [InlineData("user:{id}", new string[0], "whose parameters are none", "MC0010")]
    [InlineData("count {0}", new[] { "userId" }, "{0} names no parameter", "MC0010")]
    [InlineData("user:{ID}", new[] { "id", "Id" }, "{ID} matches the parameters id, Id when case is ignored", "MC0017")]
    [InlineData("user:{user.Id}", new[] { "user" }, "{user.Id} is not a parameter name", "MC0016")]
    [InlineData("user:{ userId }", new[] { "userId" }, "{ userId } is not a parameter name", "MC0016")]
    public void Check_PlaceholderThatWouldStayLiteral_ExplainsWhy(string tag, string[] parameters, string reason, string rule)
    {
        var problem = TagTemplateRules.Check(tag, parameters);

        Assert.NotNull(problem);
        Assert.Contains(reason, problem, StringComparison.Ordinal);
        Assert.EndsWith($"({rule})", problem, StringComparison.Ordinal);
    }
}
