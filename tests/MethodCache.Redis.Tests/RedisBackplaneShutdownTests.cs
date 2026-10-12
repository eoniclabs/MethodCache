using NSubstitute;
using StackExchange.Redis;
using Xunit;
using Microsoft.Extensions.Logging;

namespace MethodCache.Redis.Tests;

public class RedisBackplaneShutdownTests
{
    private sealed class RecordingLogger : ILogger<RedisBackplane>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => Messages.Add((level, formatter(state, error)));
    }

    [Fact]
    public async Task InvalidFrames_WarnOnceWithoutLoggingTheirContents()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        Action<RedisChannel, RedisValue>? receive = null;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Do<Action<RedisChannel, RedisValue>>(h => receive = h), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        var logger = new RecordingLogger();
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel", logger);
        var delivered = 0;
        await backplane.SubscribeAsync(_ => { delivered++; return Task.CompletedTask; });
        receive!(RedisChannel.Literal("channel"), "0\nlegacy\nsensitive-key");
        receive!(RedisChannel.Literal("channel"), "bad-frame");
        var logs = logger.Messages.ToArray();
        Assert.Single(logs);
        Assert.Equal(LogLevel.Warning, logs[0].Level);
        Assert.DoesNotContain("sensitive-key", logs[0].Message);
        Assert.Equal(0, delivered);
    }

    [Fact]
    public void Backplane_SupportsOwnedSubscriptionDisposal()
    {
        var backplane = new RedisBackplane(() => Task.FromResult(Substitute.For<IConnectionMultiplexer>()), "channel");
        Assert.IsAssignableFrom<IDisposable>(backplane);
        Assert.IsAssignableFrom<IAsyncDisposable>(backplane);
    }

    [Fact]
    public async Task Unsubscribe_ConnectionStillOpening_CannotRegisterLater()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        var opening = new TaskCompletionSource<IConnectionMultiplexer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backplane = new RedisBackplane(() => opening.Task, "channel");
        var pending = backplane.SubscribeAsync(_ => Task.CompletedTask);
        await backplane.UnsubscribeAsync();
        opening.SetResult(connection);
        await Record.ExceptionAsync(() => pending);
        await subscriber.DidNotReceive().SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task DisposeAsync_SubscriptionAckPending_DisablesCallbacksAndRemovesOnlyItsHandler()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<RedisChannel, RedisValue>? handler = null;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Do<Action<RedisChannel, RedisValue>>(h => handler = h), Arg.Any<CommandFlags>()).Returns(ack.Task);
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        var calls = 0;
        var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        var subscription = backplane.SubscribeAsync(_ => { calls++; return Task.CompletedTask; });
        var shutdown = backplane.DisposeAsync().AsTask();
        handler!(RedisChannel.Literal("channel"), RedisBackplane.Format(Extensibility.BackplaneMessageType.KeyInvalidation, "remote", "key"));
        Assert.Equal(0, calls);
        Assert.False(shutdown.IsCompleted);
        ack.SetResult();
        await subscription;
        await shutdown;
        await backplane.DisposeAsync();
        await subscriber.Received(1).UnsubscribeAsync(RedisChannel.Literal("channel"), handler, CommandFlags.None);
        connection.DidNotReceive().Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => backplane.SubscribeAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task DisposeAsync_AnEarlierUnsubscribeIsPending_WaitsForThatCleanup()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(cleanup.Task);
        var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        var unsubscribing = backplane.UnsubscribeAsync();
        var shutdown = backplane.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);
        cleanup.SetResult();
        await unsubscribing;
        await shutdown;
        connection.DidNotReceive().Dispose();
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2)]
    public async Task DisposeAsync_ServerUnreachable_DoesNotFailApplicationShutdown(int failure)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        Exception error = failure switch
        {
            0 => new RedisConnectionException(ConnectionFailureType.UnableToConnect, "offline"),
            1 => new RedisTimeoutException("offline", CommandStatus.Sent),
            _ => new ObjectDisposedException("connection")
        };
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.FromException(error));
        var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        await backplane.DisposeAsync();
        connection.DidNotReceive().Dispose();
    }
}
