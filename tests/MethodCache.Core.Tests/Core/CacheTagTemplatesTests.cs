using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class CacheTagTemplatesTests
{
    [Fact]
    public void Expand_ReplacesPlaceholdersWithValues_IgnoringCase()
    {
        var policy = Policy("users", "user:{userId}", "org:{OrgId}:user:{USERID}");

        var expanded = CacheTagTemplates.Expand(policy, new[] { "userId", "orgId" }, new[] { "42", "7" });

        Assert.Equal(new[] { "users", "user:42", "org:7:user:42" }, expanded.Tags);
    }

    [Fact]
    public void Expand_LeavesUnknownPlaceholdersAsTheyAre()
    {
        var expanded = CacheTagTemplates.Expand(Policy("item:{other}"), new[] { "id" }, new[] { "1" });

        Assert.Equal(new[] { "item:{other}" }, expanded.Tags);
    }

    [Fact]
    public void Expand_WithoutPlaceholders_ReturnsTheSamePolicy()
    {
        var policy = Policy("users");

        Assert.Same(policy, CacheTagTemplates.Expand(policy, new[] { "id" }, new[] { "1" }));
    }

    [Fact]
    public void Expand_DoesNotChangeTheOriginalPolicy()
    {
        var policy = Policy("user:{id}");

        CacheTagTemplates.Expand(policy, new[] { "id" }, new[] { "1" });

        Assert.Equal(new[] { "user:{id}" }, policy.Tags);
    }

    [Fact]
    public void Expand_KeepsArgumentTextThatLooksLikeAPlaceholder()
    {
        // Found by TagTemplatePropertyTests: the values used to be substituted one parameter at a time, so "{org}"
        // passed for id became "7", while [CacheInvalidate] (string.Format, one pass) removes "user:{org}".
        var expanded = CacheTagTemplates.Expand(Policy("user:{id}", "org:{org}"), new[] { "id", "org" }, new[] { "{org}", "7" });

        Assert.Equal(new[] { "user:{org}", "org:7" }, expanded.Tags);
    }

    [Fact]
    public void Expand_WithMismatchedNamesAndValues_Throws()
    {
        Assert.Throws<ArgumentException>(() => CacheTagTemplates.Expand(Policy("user:{id}"), new[] { "id" }, Array.Empty<string>()));
    }

    private static CacheRuntimePolicy Policy(params string[] tags) => new() { MethodId = "Test.Method", Tags = tags };
}
