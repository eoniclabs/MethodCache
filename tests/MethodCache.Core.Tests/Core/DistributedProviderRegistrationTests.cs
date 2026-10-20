extern alias SqlProvider;

using System.Collections.Concurrent;
using MethodCache.TestSupport;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlProvider::Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Core;

/// <summary>
/// MethodCache uses one distributed cache (L2): a second provider is rejected when it is added, and settings that
/// only apply to a distributed cache log a warning at startup when there is none.
/// </summary>
public class DistributedProviderRegistrationTests
{
    private const string ConnectionString = "Server=localhost;Database=MethodCache;Integrated Security=true;TrustServerCertificate=true";

    [Fact]
    public void AddMethodCache_SecondDistributedCacheProvider_IsRejectedWhenAdded()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseSqlServer(ConnectionString));

        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCacheDistributedCache(_ => new AtomicTestCache()));

        Assert.Contains("one distributed cache", error.Message);
        Assert.Contains("MethodCache.SqlServer", error.Message);
        Assert.Contains("MethodCache.Core.Tests", error.Message);
        Assert.Contains("UseRedis or UseSqlServer", error.Message);
    }

    [Fact]
    public void AddMethodCache_ProviderAfterAnotherInOneBuilder_IsRejected()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddMethodCache(mc =>
        {
            mc.Services.AddMethodCacheDistributedCache(_ => new AtomicTestCache());
            mc.UseSqlServer(ConnectionString);
        }));

        Assert.Contains("MethodCache.SqlServer", error.Message);
    }

    [Fact]
    public void AddMethodCache_SameProviderTwice_AddsToItsOptions()
    {
        var services = new ServiceCollection();
        services.AddMethodCache(mc => mc.UseSqlServer(ConnectionString));
        services.AddMethodCache(mc => mc.UseSqlServer(options => options.EnableBackplane = false));
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqlProvider::MethodCache.SqlServer.SqlServerDistributedCache>(provider.GetRequiredKeyedService<IDistributedCache>("MethodCache"));
        Assert.Null(provider.GetKeyedService<MethodCache.Extensibility.IBackplane>("MethodCache"));
    }

    [Theory]
    [InlineData("distributed", "Distributed(...) does nothing")]
    [InlineData("messagepack", "The serializer chosen with UseMessagePack or UseJson does nothing")]
    [InlineData("both", "Distributed(...) and the serializer chosen with UseMessagePack or UseJson do nothing")]
    public async Task StartHost_DistributedSettingsWithoutADistributedCache_LogsOneWarning(string settings, string expected)
    {
        var logs = new Logs();
        using var host = BuildHost(logs, services => services.AddSingleton<IWarningPrices, WarningPrices>().AddMethodCache(mc =>
        {
            if (settings is "distributed" or "both") mc.Distributed(options => options.KeyPrefix = "app:");
            if (settings is "messagepack" or "both") mc.UseMessagePack();
        }));

        await host.StartAsync();
        host.Services.GetRequiredService<IWarningPrices>().Get(1);
        await host.StopAsync();

        var warning = Assert.Single(logs.Messages, message => message.Level == LogLevel.Warning).Text;
        Assert.StartsWith(expected, warning, StringComparison.Ordinal);
        Assert.Contains("UseRedis(...) or UseSqlServer(...)", warning);
    }

    [Fact]
    public async Task StartHost_DistributedSettingsWithADistributedCache_LogsNoWarning()
    {
        var logs = new Logs();
        using var host = BuildHost(logs, services => services
            .AddMethodCache(mc => mc.Distributed(options => options.KeyPrefix = "app:").UseMessagePack())
            .AddMethodCacheDistributedCache(_ => new AtomicTestCache()));

        await host.StartAsync();
        await host.StopAsync();

        Assert.DoesNotContain(logs.Messages, message => message.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task StartHost_NoDistributedSettings_LogsNoWarning()
    {
        var logs = new Logs();
        using var host = BuildHost(logs, services => services.AddMethodCache());

        await host.StartAsync();
        await host.StopAsync();

        Assert.DoesNotContain(logs.Messages, message => message.Level == LogLevel.Warning);
    }

    private static IHost BuildHost(Logs logs, Action<IServiceCollection> configure) => new HostBuilder()
        .ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs))
        .ConfigureServices(configure)
        .Build();

    public interface IWarningPrices
    {
        [Cache] int Get(int id);
    }

    public sealed class WarningPrices : IWarningPrices
    {
        public int Get(int id) => id;
    }

    private sealed class Logs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _messages = new();
        internal IReadOnlyCollection<(LogLevel Level, string Text)> Messages => _messages.Where(static m => m.Text.Length > 0).ToArray();
        public ILogger CreateLogger(string categoryName) => new Sink(_messages, categoryName);
        public void Dispose() { }

        private sealed class Sink(ConcurrentQueue<(LogLevel, string)> messages, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            {
                if (category.StartsWith("MethodCache", StringComparison.Ordinal)) messages.Enqueue((level, formatter(state, error)));
            }
        }
    }
}
