using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MethodCache.HttpCaching.Tests;

public class HttpClientDefaultsWarningTests
{
    [Fact]
    public void DefaultsCookiePolicy_LogsOnceForEachFinalNamedClient()
    {
        var logs = new CookieLogs(); var services = new ServiceCollection();
        services.AddLogging(o => o.AddProvider(logs)); services.AddMethodCache();
        services.ConfigureHttpClientDefaults(b => b.AddHttpCaching()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = true }));
        using var provider = services.BuildServiceProvider(); var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var first = factory.CreateClient("a"); using var second = factory.CreateClient("b");
        using var same = factory.CreateClient("a");
        Assert.Equal(2, logs.Count);
    }

    private sealed class CookieLogs : ILoggerProvider, ILogger
    {
        internal int Count;
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (format(state, error).StartsWith("HTTP caching disabled automatic transport cookies.", StringComparison.Ordinal)) Interlocked.Increment(ref Count);
        }
    }
}
