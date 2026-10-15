using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class DurationGrammarTests
{
    [Theory]
    [MemberData(nameof(MethodCache.TestSupport.DurationCorpus.Values), MemberType = typeof(MethodCache.TestSupport.DurationCorpus))]
    public void Configuration_UsesTheInvariantDurationGrammar(string text, long ticks)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["MethodCache:Defaults:Duration"] = text }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IReader, Reader>();
        services.AddMethodCache(builder => builder.FromAssemblyOf<DurationGrammarTests>().FromConfiguration(configuration, "MethodCache"));
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<MethodCache.Policies.IPolicyRegistry>();
        Assert.Equal(TimeSpan.FromTicks(ticks), registry.GetPolicy(typeof(IReader).FullName!.Replace('+', '.') + ".Read").Policy.Duration);
    }
    [Theory]
    [MemberData(nameof(MethodCache.TestSupport.DurationCorpus.Invalid), MemberType = typeof(MethodCache.TestSupport.DurationCorpus))]
    public void Configuration_InvalidExplicitDuration_ReportsTheSetting(string text)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["MethodCache:Defaults:Duration"] = text }).Build();
        var services = new ServiceCollection();
        services.AddMethodCache(builder => builder.FromConfiguration(configuration, "MethodCache"));
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<MethodCache.Policies.IPolicyRegistry>().GetPolicy("Sample.Type.Method"));
        Assert.Contains("MethodCache:Defaults:Duration", error.Message, StringComparison.Ordinal);
    }

    public interface IReader { [Cache] int Read(); }
    public sealed class Reader : IReader { public int Read() => 1; }
}
