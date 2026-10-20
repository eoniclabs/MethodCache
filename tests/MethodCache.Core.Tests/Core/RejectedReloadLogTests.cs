using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// A reloaded appsettings section that is rejected is logged with the path of the offending setting and what is
/// wrong with it, never with its value.
/// </summary>
public class RejectedReloadLogTests
{
    private const string Secret = "token=secret-canary";

    [Theory]
    [InlineData("MethodCache:Defaults:Duration", "'MethodCache:Defaults:Duration' is not a positive time span such as \"00:05:00\"")]
    [InlineData("MethodCache:Defaults:Enabled", "'MethodCache:Defaults:Enabled' is not true or false")]
    [InlineData("MethodCache:Defaults:Version", "'MethodCache:Defaults:Version' is not a non-negative integer")]
    [InlineData("MethodCache:Defaults:Durration", "'MethodCache:Defaults:Durration' is not a setting MethodCache knows")]
    public async Task Reload_InvalidValue_LogsThePathAndReasonButNotTheValue(string key, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Defaults:Duration"] = "00:01:00"
        }).Build();
        var logs = new Logs();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddSingleton<IReloadPrices, ReloadPrices>();
        services.AddMethodCache(mc => mc.FromConfiguration(configuration));
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IReloadPrices>().Get(1);
        Assert.Equal(TimeSpan.FromMinutes(1), Duration(provider));

        configuration[key] = Secret;
        configuration.Reload();

        var message = Assert.Single(logs.Messages);
        Assert.Contains(expected, message);
        Assert.StartsWith("Ignored reloaded MethodCache configuration", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", message);
        Assert.Equal(TimeSpan.FromMinutes(1), Duration(provider));
    }

    private static TimeSpan? Duration(IServiceProvider provider) => provider.GetRequiredService<ICacheAdministrator>().GetMethods()
        .Single(method => method.MethodId.EndsWith("IReloadPrices.Get", StringComparison.Ordinal)).Options.Duration;

    public interface IReloadPrices
    {
        [Cache] int Get(int id);
    }

    public sealed class ReloadPrices : IReloadPrices
    {
        public int Get(int id) => id;
    }

    [Fact]
    public void Start_InvalidValue_StillNamesTheValueForTheDeveloper()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MethodCache:Defaults:Duration"] = "soon"
        }).Build();
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.FromConfiguration(configuration));
        using var provider = services.BuildServiceProvider();

        var error = Assert.ThrowsAny<Exception>(() => provider.GetRequiredService<MethodCache.Policies.IPolicyRegistry>().GetPolicy("Sample.Read"));

        Assert.Contains("'soon'", error.ToString());
    }

    private sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();
        internal IReadOnlyCollection<string> Messages => _messages.ToArray();
        public ILogger CreateLogger(string categoryName) => new Sink(_messages);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                => messages.Enqueue(formatter(state, error) + error);
        }
    }
}
