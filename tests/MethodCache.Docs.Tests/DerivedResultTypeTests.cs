using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// docs/features/distributed-cache.md#serialization: a derived type where a cached result declares its base type, such
/// as a <c>Dog</c> in a <c>List&lt;Animal&gt;</c>, comes back from the distributed cache as itself, since MethodCache's
/// generator names the project's derived types to its JSON serializer. The application's own JSON is unchanged.
/// </summary>
public class DerivedResultTypeTests
{
    public class Animal
    {
        public string Name { get; set; } = "";
    }

    public class Dog : Animal
    {
        public string Breed { get; set; } = "";
    }

    public sealed class Zoo
    {
        public Animal? Star { get; set; }

        public List<Animal> Animals { get; set; } = [];
    }

    public interface IZoos
    {
        [Cache] Task<List<Animal>> AnimalsAsync(int id);

        [Cache] Task<Zoo> ZooAsync(int id);

        [Cache] Task<Animal> StarAsync(int id);
    }

    private sealed class Zoos : IZoos
    {
        public int Calls;

        public Task<List<Animal>> AnimalsAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new List<Animal> { new() { Name = "cat" }, new Dog { Name = "rex", Breed = "collie" } });
        }

        public Task<Zoo> ZooAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new Zoo { Star = new Dog { Name = "rex", Breed = "collie" }, Animals = [new Dog { Name = "fido", Breed = "pug" }] });
        }

        public Task<Animal> StarAsync(int id)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<Animal>(new Dog { Name = "rex", Breed = "collie" });
        }
    }

    private readonly MethodCache.TestSupport.AtomicTestCache _shared;
    private readonly FakeTimeProvider _time = new();

    public DerivedResultTypeTests() => _shared = new(_time);

    private (ServiceProvider Provider, Zoos Zoos, WarningLog Log) Instance()
    {
        var zoos = new Zoos();
        var log = new WarningLog();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddLogging(logging => logging.AddProvider(log));
        services.AddSingleton<IZoos>(zoos);
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => _shared);
        return (services.BuildServiceProvider(), zoos, log);
    }

    [Fact]
    public async Task DerivedTypes_ComeBackFromTheDistributedCacheAsThemselves()
    {
        var (firstProvider, first, firstLog) = Instance();
        var (secondProvider, second, secondLog) = Instance();
        using (firstProvider)
        using (secondProvider)
        {
            var a = firstProvider.GetRequiredService<IZoos>();
            await a.AnimalsAsync(1);
            await a.ZooAsync(1);
            await a.StarAsync(1);
            Assert.Equal(3, first.Calls);

            var b = secondProvider.GetRequiredService<IZoos>();
            var animals = await b.AnimalsAsync(1);
            Assert.IsType<Animal>(animals[0]);
            Assert.Equal("collie", Assert.IsType<Dog>(animals[1]).Breed);
            var zoo = await b.ZooAsync(1);
            Assert.Equal("collie", Assert.IsType<Dog>(zoo.Star).Breed);
            Assert.Equal("pug", Assert.IsType<Dog>(Assert.Single(zoo.Animals)).Breed);
            Assert.Equal("collie", Assert.IsType<Dog>(await b.StarAsync(1)).Breed);
            Assert.Equal(0, second.Calls);

            Assert.Empty(firstLog.Warnings);
            Assert.Empty(secondLog.Warnings);
        }
    }

    [Fact]
    public void TheApplicationsOwnJson_IsUnchanged()
    {
        using var provider = Instance().Provider;

        var json = JsonSerializer.Serialize<Animal>(new Dog { Name = "rex", Breed = "collie" });

        Assert.Equal("""{"Name":"rex"}""", json);
    }

    private sealed class WarningLog : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Enqueue(formatter(state, exception));
        }

        public void Dispose()
        {
        }
    }
}
