using NSubstitute;
using StackExchange.Redis;
using Xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

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
        receive!(RedisChannel.Literal("channel"), "0\nsender\nsensitive-key");
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

    [Fact]
    public async Task Subscribe_RedisDownAtFirst_RetriesWithBackoffAndThenDeliversInvalidations()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        Action<RedisChannel, RedisValue>? receive = null;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Do<Action<RedisChannel, RedisValue>>(h => receive = h), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);

        // Redis is down for the first two connects
        var attempts = 0;
        Task<IConnectionMultiplexer> Connect() => ++attempts <= 2
            ? Task.FromException<IConnectionMultiplexer>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"))
            : Task.FromResult(connection);
        var time = new TimerCountingTimeProvider();
        var logger = new RecordingLogger();
        await using var backplane = new RedisBackplane(Connect, "channel", logger, time);
        var delivered = new List<string?>();
        var subscription = backplane.SubscribeAsync(message =>
        {
            if (message.Type == Extensibility.BackplaneMessageType.KeyInvalidation) delivered.Add(message.Key);
            return Task.CompletedTask;
        });

        // Each wait is advanced only once its timer exists: advancing earlier would not count toward it
        await WaitUntil(() => attempts == 1 && time.Timers == 1);
        Assert.False(subscription.IsCompleted);
        time.Advance(RedisBackplane.FirstRetryDelay);
        await WaitUntil(() => attempts == 2 && time.Timers == 2);
        time.Advance(RedisBackplane.FirstRetryDelay); // the second wait is twice as long
        Assert.Equal(2, attempts);
        time.Advance(RedisBackplane.FirstRetryDelay);
        await subscription.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, attempts);
        Assert.Equal(2, logger.Messages.Count(m => m.Level == LogLevel.Warning));
        receive!(RedisChannel.Literal("channel"), RedisBackplane.Format(Extensibility.BackplaneMessageType.KeyInvalidation, "remote", "key"));
        Assert.Equal(new[] { "key" }, delivered);
    }

    [Fact]
    public async Task Subscribe_AfterFailedAttempts_ClearsWhatThisInstanceCachedWhileItMissedInvalidations()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);

        var attempts = 0;
        Task<IConnectionMultiplexer> Connect() => ++attempts == 1
            ? Task.FromException<IConnectionMultiplexer>(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"))
            : Task.FromResult(connection);
        var time = new TimerCountingTimeProvider();
        await using var backplane = new RedisBackplane(Connect, "channel", timeProvider: time);
        var delivered = new List<Extensibility.BackplaneMessageType>();
        var subscription = backplane.SubscribeAsync(message => { lock (delivered) delivered.Add(message.Type); return Task.CompletedTask; });

        await WaitUntil(() => attempts == 1 && time.Timers == 1);
        time.Advance(RedisBackplane.FirstRetryDelay);
        await subscription.WaitAsync(TimeSpan.FromSeconds(10));

        lock (delivered) Assert.Equal(new[] { Extensibility.BackplaneMessageType.ClearAll }, delivered);
    }

    [Fact]
    public async Task Subscribe_ThatTimesOut_IsTriedAgain_AndThenClearsWhatWasMissed()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        var subscribes = 0;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++subscribes == 1 ? Task.FromException(new RedisTimeoutException("timeout", CommandStatus.Sent)) : Task.CompletedTask);
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);

        var time = new TimerCountingTimeProvider();
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel", timeProvider: time);
        var delivered = new List<Extensibility.BackplaneMessageType>();
        var subscription = backplane.SubscribeAsync(message => { lock (delivered) delivered.Add(message.Type); return Task.CompletedTask; });

        await WaitUntil(() => subscribes == 1 && time.Timers == 1);
        Assert.False(subscription.IsCompleted);
        time.Advance(RedisBackplane.FirstRetryDelay);
        await subscription.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, subscribes);
        lock (delivered) Assert.Equal(new[] { Extensibility.BackplaneMessageType.ClearAll }, delivered);
        await subscriber.Received(1).UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task Subscribe_AtTheFirstAttempt_ClearsNothing()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        var delivered = 0;

        await backplane.SubscribeAsync(_ => { Interlocked.Increment(ref delivered); return Task.CompletedTask; });

        Assert.Equal(0, delivered);
    }

    [Fact]
    public async Task Dispose_WhileWaitingToRetry_EndsTheSubscription()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;
        var backplane = new RedisBackplane(() => { attempts++; throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"); }, "channel", timeProvider: time);
        var subscription = backplane.SubscribeAsync(_ => Task.CompletedTask);
        await WaitUntil(() => attempts == 1);

        await backplane.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => subscription.WaitAsync(TimeSpan.FromSeconds(10)));
        time.Advance(RedisBackplane.MaxRetryDelay);
        Assert.Equal(1, attempts);
    }

    /// <summary>A fake clock that counts the timers created on it.</summary>
    private sealed class TimerCountingTimeProvider : FakeTimeProvider
    {
        private int _timers;

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(5);
        }
    }
}
