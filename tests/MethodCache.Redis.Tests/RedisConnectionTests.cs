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

    [Fact]
    public async Task GetAsync_FactoryBlocks_CallersReturnAtOnce_AndShareTheOneAttempt()
    {
        // The app's factory blocks while it connects, as ConnectionMultiplexer.Connect does while Redis is slow
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var options = new RedisOptions
        {
            ConnectionMultiplexerFactory = _ =>
            {
                Interlocked.Increment(ref calls);
                gate.Task.Wait();
                return multiplexer;
            },
        };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var connection = new RedisConnection(options, services);
        try
        {
            // Each caller on its own thread: one the factory blocks fails the test at the timeout instead of hanging it
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CallOnOwnThread(connection)))
                .WaitAsync(TimeSpan.FromSeconds(10));

            attempts.Distinct().Should().ContainSingle("concurrent callers share one connection attempt");
            attempts[0].IsCompleted.Should().BeFalse();
            gate.SetResult();
            (await attempts[0]).Should().BeSameAs(multiplexer);
            calls.Should().Be(1);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task GetAsync_SharedAttemptFails_EveryCallerSeesIt_AndTheNextCallConnectsAgain()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var options = new RedisOptions
        {
            ConnectionMultiplexerFactory = _ =>
            {
                if (Interlocked.Increment(ref calls) > 1) return multiplexer;
                gate.Task.Wait();
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
            },
        };
        using var services = new ServiceCollection().BuildServiceProvider();
        using var connection = new RedisConnection(options, services);
        try
        {
            var attempts = await Task.WhenAll(CallOnOwnThread(connection), CallOnOwnThread(connection)).WaitAsync(TimeSpan.FromSeconds(10));
            attempts[1].Should().BeSameAs(attempts[0]);
            gate.SetResult();

            // The cache treats the failure as Redis being unavailable and serves from L1; the next call tries again
            await Assert.ThrowsAsync<RedisConnectionException>(() => attempts[0]);
            await Assert.ThrowsAsync<RedisConnectionException>(() => attempts[1]);
            (await connection.GetAsync()).Should().BeSameAs(multiplexer);
            calls.Should().Be(2);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    // StartNew, not Task.Run: Task.Run would unwrap the attempt, and the test needs the attempt itself
    private static Task<Task<IConnectionMultiplexer>> CallOnOwnThread(RedisConnection connection)
        => Task.Factory.StartNew(connection.GetAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
