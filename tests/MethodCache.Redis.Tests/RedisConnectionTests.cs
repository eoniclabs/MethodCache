using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.Tests;

/// <summary>How MethodCache opens and reopens its Redis connection.</summary>
public class RedisConnectionTests
{
    [Fact]
    public async Task GetAsync_FactoryFailedOnce_AsksTheFactoryAgain()
    {
        // The app's factory: Redis is down at the first call (ConnectionMultiplexer.Connect throws), up at the next
        var calls = 0;
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var options = new RedisOptions
        {
            ConnectionMultiplexerFactory = _ => ++calls == 1
                ? throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down")
                : multiplexer,
        };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var connection = new RedisConnection(options, services);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.GetAsync());

        (await connection.GetAsync()).Should().BeSameAs(multiplexer, "a connection that failed once must be retried, not cached as failed");
    }
}
