using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// A coding agent starts from docs/ai/methodcache.md. A guide it doesn't list is one the agent won't find, as happened
/// with sharing only invalidations, which the distributed-cache guide had and the agent guide didn't mention.
/// </summary>
public class AgentGuideTests
{
    public static TheoryData<string> FeatureGuides()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(DemoWebFactory.RepositoryRoot, "docs", "features"), "*.md").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(FeatureGuides))]
    public void EveryFeatureGuide_IsListedInTheAgentGuide(string guide)
    {
        var agentGuide = File.ReadAllText(Path.Combine(DemoWebFactory.RepositoryRoot, "docs", "ai", "methodcache.md"));

        Assert.True(agentGuide.Contains($"`{guide}`", StringComparison.Ordinal),
            $"docs/ai/methodcache.md doesn't list docs/features/{guide}. Add it under \"Guides\" with what it covers.");
    }
}
