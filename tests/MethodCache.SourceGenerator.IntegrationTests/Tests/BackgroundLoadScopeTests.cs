using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MethodCache.SourceGenerator.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.SourceGenerator.IntegrationTests.Tests;

/// <summary>
/// A refresh-ahead load, and a fail-safe load that continues after its caller got the stale value, outlive the
/// request that started them. For a scoped or transient service they run on a new instance from a new scope, which
/// is disposed when the load ends, never on the instance of a request whose scope already ended
/// (docs/features/expiration.md, docs/features/fail-safe.md).
/// </summary>
public class BackgroundLoadScopeTests
{
    private const string Source = @"
using System;
using System.Threading;
using System.Threading.Tasks;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundScope
{
    public interface IPrices
    {
        [Cache(Duration = ""00:01:00"", RefreshAhead = ""00:00:30"")]
        Task<int> GetRefreshedAsync(int id);

        [Cache(Duration = ""00:01:00"", FailSafe = ""01:00:00"", FailSafeTimeout = ""00:00:00.1"")]
        Task<int> GetFailSafeAsync(int id);
    }

    // Stands in for a repository that uses a scoped DbContext
    public class Prices : IPrices, IDisposable
    {
        public static TaskCompletionSource<int> Gate = new TaskCompletionSource<int>();
        public static int UsedAfterDispose;
        public static int Finished;
        public static int Created;
        public static Prices LastLoader;

        private volatile bool _disposed;

        public Prices() : this(null) { }

        public Prices(object key)
        {
            Key = key;
            Interlocked.Increment(ref Created);
        }

        public object Key { get; }

        public bool Disposed => _disposed;

        public static void Reset(int? completed)
        {
            Gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (completed is int value) Gate.SetResult(value);
        }

        public static void Complete(int value) => Gate.SetResult(value);

        public Task<int> GetRefreshedAsync(int id) => LoadAsync();

        public Task<int> GetFailSafeAsync(int id) => LoadAsync();

        private async Task<int> LoadAsync()
        {
            var value = await Gate.Task;
            if (_disposed) Interlocked.Increment(ref UsedAfterDispose);
            LastLoader = this;
            Interlocked.Increment(ref Finished);
            return value;
        }

        public void Dispose() => _disposed = true;
    }

    public static class Startup
    {
        public static void Configure(IServiceCollection services, string lifetime)
        {
            switch (lifetime)
            {
                case ""scoped"": services.AddScoped<IPrices, Prices>(); break;
                case ""transient"": services.AddTransient<IPrices, Prices>(); break;
                case ""singleton"": services.AddSingleton<IPrices, Prices>(); break;
                case ""keyed-scoped"": services.AddKeyedScoped<IPrices>(""eu"", (sp, key) => new Prices(key)); break;
            }

            services.AddMethodCache();
        }
    }
}";

    private readonly SourceGeneratorTestEngine _engine = new();

    [Theory]
    [InlineData("scoped", "GetRefreshedAsync", 40)]
    [InlineData("transient", "GetRefreshedAsync", 40)]
    [InlineData("keyed-scoped", "GetRefreshedAsync", 40)]
    [InlineData("scoped", "GetFailSafeAsync", 61)]
    [InlineData("keyed-scoped", "GetFailSafeAsync", 61)]
    public async Task BackgroundLoad_OfAScopedOrTransientService_RunsOnANewInstanceInItsOwnScope(string lifetime, string method, int advanceSeconds)
    {
        var app = await StartAsync(lifetime, method);

        // Request 1 loads the value
        app.Reset(1);
        await using (var scope = app.Provider.CreateAsyncScope())
        {
            Assert.Equal(1, await app.InvokeAsync(scope.ServiceProvider));
        }

        // Request 2 gets the cached (or fail-safe) value and ends while the load keeps running
        app.Reset(null);
        app.Time.Advance(TimeSpan.FromSeconds(advanceSeconds));
        object requestInstance;
        await using (var scope = app.Provider.CreateAsyncScope())
        {
            Assert.Equal(1, await app.InvokeAsync(scope.ServiceProvider));
            requestInstance = app.LastCreated(scope.ServiceProvider);
        }

        app.Complete(2);
        await app.WaitForAsync(() => app.Finished >= 2, "The background load did not run.");

        Assert.True(app.UsedAfterDispose == 0, $"The load ran on an instance whose scope was disposed ({app.UsedAfterDispose} time(s)).");
        var loader = app.LastLoader;
        Assert.NotSame(requestInstance, loader);
        if (lifetime == "keyed-scoped")
        {
            Assert.Equal("eu", app.KeyOf(loader));
        }

        // The background scope ends with the load
        await app.WaitForAsync(() => app.IsDisposed(loader), "The background load's scope was not disposed.");

        // The loaded value reached the cache
        await using (var scope = app.Provider.CreateAsyncScope())
        {
            await app.WaitForAsync(async () => await app.InvokeAsync(scope.ServiceProvider) == 2, "The loaded value was not stored.");
        }
    }

    [Fact]
    public async Task BackgroundRefresh_OfASingleton_UsesTheSingleton()
    {
        var app = await StartAsync("singleton", "GetRefreshedAsync");

        app.Reset(1);
        Assert.Equal(1, await app.InvokeAsync(app.Provider));

        app.Reset(null);
        app.Time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(1, await app.InvokeAsync(app.Provider));

        app.Complete(2);
        await app.WaitForAsync(() => app.Finished >= 2, "The background refresh did not run.");

        Assert.Equal(1, app.Created);
        Assert.False(app.IsDisposed(app.LastLoader));
    }

    private async Task<App> StartAsync(string lifetime, string method)
    {
        var compiled = await _engine.CompileWithSourceGeneratorAsync(Source, "BackgroundScope_" + lifetime.Replace('-', '_') + "_" + method);
        var services = new ServiceCollection();
        var time = new ManualTimeProvider();
        services.AddSingleton<TimeProvider>(time);
        compiled.Assembly.GetType("BackgroundScope.Startup")!.GetMethod("Configure")!.Invoke(null, new object[] { services, lifetime });
        return new App(compiled.Assembly, services.BuildServiceProvider(), time, lifetime, method);
    }

    private sealed class App(Assembly assembly, ServiceProvider provider, ManualTimeProvider time, string lifetime, string method)
    {
        private readonly Type _interface = assembly.GetType("BackgroundScope.IPrices")!;
        private readonly Type _prices = assembly.GetType("BackgroundScope.Prices")!;

        public ServiceProvider Provider => provider;

        public ManualTimeProvider Time => time;

        public int Finished => (int)Static("Finished")!;

        public int UsedAfterDispose => (int)Static("UsedAfterDispose")!;

        public int Created => (int)Static("Created")!;

        public object LastLoader => Static("LastLoader")!;

        public void Reset(int? completed) => _prices.GetMethod("Reset")!.Invoke(null, new object?[] { completed });

        public void Complete(int value) => _prices.GetMethod("Complete")!.Invoke(null, new object[] { value });

        public bool IsDisposed(object prices) => (bool)_prices.GetProperty("Disposed")!.GetValue(prices)!;

        public object? KeyOf(object prices) => _prices.GetProperty("Key")!.GetValue(prices);

        public Task<int> InvokeAsync(IServiceProvider services)
        {
            var service = Resolve(services);
            return (Task<int>)InterfaceMethods.Find(service, method).Invoke(service, new object[] { 1 })!;
        }

        /// <summary>The implementation instance the request's scope holds, behind its decorator.</summary>
        public object LastCreated(IServiceProvider services)
        {
            var decorator = Resolve(services);
            var field = decorator.GetType().GetField("_decorated", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return field.GetValue(decorator)!;
        }

        public async Task WaitForAsync(Func<bool> condition, string message)
            => await WaitForAsync(() => Task.FromResult(condition()), message);

        public async Task WaitForAsync(Func<Task<bool>> condition, string message)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!await condition())
            {
                Assert.True(DateTime.UtcNow < deadline, message);
                await Task.Delay(10);
            }
        }

        private object Resolve(IServiceProvider services)
            => lifetime == "keyed-scoped" ? services.GetRequiredKeyedService(_interface, "eu") : services.GetRequiredService(_interface);

        private object? Static(string name) => _prices.GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
