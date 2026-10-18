using MethodCache.Extensibility;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.Tests;

public class BackplaneStateTests
{
    [Fact]
    public async Task SubscriptionState_IsIndependentOfInteractiveCacheConnectivity_AndWaitsForRecoveryClear()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        Assert.Equal(BackplaneState.Connecting, backplane.ConnectionState);
        var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clearing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await backplane.SubscribeAsync(_ => { clearing.TrySetResult(); return recovery.Task; });
        Assert.Equal(BackplaneState.Connected, backplane.ConnectionState);
        var interactive = Failure(connection, ConnectionType.Interactive);
        connection.ConnectionFailed += Raise.EventWith(interactive);
        Assert.Equal(BackplaneState.Connected, backplane.ConnectionState);
        var subscription = Failure(connection, ConnectionType.Subscription);
        connection.ConnectionFailed += Raise.EventWith(subscription);
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
        connection.ConnectionRestored += Raise.EventWith(subscription);
        await clearing.Task;
        Assert.Equal(BackplaneState.Connecting, backplane.ConnectionState);
        recovery.SetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (backplane.ConnectionState != BackplaneState.Connected) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        await backplane.UnsubscribeAsync();
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
    }

    [Fact]
    public async Task MissingSubscriptionAcknowledgement_IsNotReportedConnected()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "offline")));
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
    }

    [Fact]
    public async Task RestoredSocket_WaitsForSubscriptionAcknowledgementBeforeReportingConnected()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => Interlocked.Increment(ref attempts) == 1 ? Task.CompletedTask : ack.Task);
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        connection.ConnectionFailed += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        Assert.Equal(BackplaneState.Connecting, backplane.ConnectionState);
        Assert.Equal(2, attempts);
        ack.SetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (backplane.ConnectionState != BackplaneState.Connected) { timeout.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
    }

    [Fact]
    public async Task NewFailureDuringRecovery_IsNotOverwrittenByOlderClearCompletion()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()).Returns(Task.CompletedTask);
        var clear = new TaskCompletionSource();
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        await backplane.SubscribeAsync(_ => clear.Task);
        connection.ConnectionFailed += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        Assert.Equal(BackplaneState.Connecting, backplane.ConnectionState);
        connection.ConnectionFailed += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        clear.SetResult();
        await backplane.RecoveryCompletion;
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
    }

    [Fact]
    public async Task RepeatedRecovery_KeepsOneDeliveryAndUnsubscribesBorrowedConnection()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        Action<RedisChannel, RedisValue>? handlers = null;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(call => { handlers += call.ArgAt<Action<RedisChannel, RedisValue>>(1); return Task.CompletedTask; });
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(call => { handlers -= call.ArgAt<Action<RedisChannel, RedisValue>>(1); return Task.CompletedTask; });
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        var deliveries = 0;
        await backplane.SubscribeAsync(message =>
        {
            if (message.Type == BackplaneMessageType.KeyInvalidation) deliveries++;
            return Task.CompletedTask;
        });
        for (var i = 0; i < 2; i++)
        {
            connection.ConnectionFailed += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
            connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
            await backplane.RecoveryCompletion;
        }
        handlers!(RedisChannel.Literal("channel"), RedisBackplane.Format(BackplaneMessageType.KeyInvalidation, "other", "key"));
        Assert.Equal(1, deliveries);
        await backplane.UnsubscribeAsync();
        Assert.Null(handlers);
        connection.DidNotReceive().Dispose();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task RecoveryHandshakeFailsOnce_RetriesWithoutAnotherSocketEvent(bool failUnsubscribe, int failureKind)
    {
        var time = new FakeTimeProvider();
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        Exception failure = failureKind switch
        {
            0 => new RedisConnectionException(ConnectionFailureType.UnableToConnect, "subscription still recovering"),
            1 => new RedisTimeoutException("subscription still recovering", CommandStatus.Sent),
            _ => new TaskCanceledException("subscription backlog")
        };
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expectedAttempts = failUnsubscribe ? 2 : 3;
        var attempts = 0;
        var removals = 0;
        subscriber.UnsubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => failUnsubscribe && Interlocked.Increment(ref removals) == 1 ? Task.FromException(failure) : Task.CompletedTask);
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (!failUnsubscribe && attempt == 2) return Task.FromException(failure);
                if (attempt == expectedAttempts) retried.TrySetResult();
                return Task.CompletedTask;
            });
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel", timeProvider: time);
        var clears = 0;
        await backplane.SubscribeAsync(_ => { Interlocked.Increment(ref clears); return Task.CompletedTask; });
        connection.ConnectionFailed += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
        time.Advance(RedisBackplane.FirstRetryDelay);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await backplane.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(1, clears);
        Assert.Equal(BackplaneState.Connected, backplane.ConnectionState);
    }

    [Fact]
    public async Task UnsubscribeDuringRecoveryRetry_CancelsTheTimerWithoutAnotherAttempt()
    {
        var time = new FakeTimeProvider();
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        var attempts = 0;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++attempts == 1 ? Task.CompletedTask : Task.FromException(new RedisTimeoutException("offline", CommandStatus.Sent)));
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel", timeProvider: time);
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
        var recovery = backplane.RecoveryCompletion;
        await backplane.UnsubscribeAsync();
        time.Advance(RedisBackplane.MaxRetryDelay);
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts);
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
    }

    [Fact]
    public async Task NewRecovery_DoesNotLetAnOlderRetrySubscribeAgain()
    {
        var time = new FakeTimeProvider();
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        var attempts = 0;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++attempts == 2 ? Task.FromException(new RedisTimeoutException("offline", CommandStatus.Sent)) : Task.CompletedTask);
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel", timeProvider: time);
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        var oldRecovery = backplane.RecoveryCompletion;
        connection.ConnectionFailed += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        await backplane.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(RedisBackplane.MaxRetryDelay);
        await oldRecovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, attempts);
        Assert.Equal(BackplaneState.Connected, backplane.ConnectionState);
    }

    [Fact]
    public async Task NonTransientRecoveryFailure_IsReportedWithoutRetrying()
    {
        var time = new FakeTimeProvider();
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        var attempts = 0;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++attempts == 2 ? Task.FromException(new InvalidOperationException("invalid subscriber")) : Task.CompletedTask);
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel", timeProvider: time);
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backplane.RecoveryCompletion);
        time.Advance(RedisBackplane.MaxRetryDelay);
        Assert.Equal(2, attempts);
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
    }

    [Fact]
    public async Task UnsubscribeDuringRecoveryAcknowledgement_CancelsTheWait()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++attempts == 1 ? Task.CompletedTask : ack.Task);
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel");
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        Assert.Equal(BackplaneState.Connecting, backplane.ConnectionState);
        var recovery = backplane.RecoveryCompletion;
        await backplane.UnsubscribeAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(BackplaneState.Disconnected, backplane.ConnectionState);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RecoveryRetry_BackoffDoublesAndStopsAtThirtySeconds()
    {
        var time = new RecoveryClock();
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber(Arg.Any<object>()).Returns(subscriber);
        var attempts = 0;
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(_ => Interlocked.Increment(ref attempts) is 1 or 9 ? Task.CompletedTask : Task.FromException(new RedisTimeoutException("offline", CommandStatus.Sent)));
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "channel", timeProvider: time);
        await backplane.SubscribeAsync(_ => Task.CompletedTask);
        connection.ConnectionRestored += Raise.EventWith(Failure(connection, ConnectionType.Subscription));
        var expected = new[] { 1, 2, 4, 8, 16, 30, 30 };
        for (var i = 0; i < expected.Length; i++)
        {
            var delay = await time.Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimeSpan.FromSeconds(expected[i]), delay);
            time.Advance(delay - TimeSpan.FromTicks(1));
            Assert.Equal(i + 2, Volatile.Read(ref attempts));
            time.Advance(TimeSpan.FromTicks(1));
        }
        await backplane.RecoveryCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(9, attempts);
        Assert.Equal(BackplaneState.Connected, backplane.ConnectionState);
    }

    private sealed class RecoveryClock : FakeTimeProvider
    {
        public global::System.Threading.Channels.Channel<TimeSpan> Delays { get; } = global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Delays.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private static ConnectionFailedEventArgs Failure(object connection, ConnectionType type)
        => new(connection, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6379), type,
            ConnectionFailureType.SocketFailure, new InvalidOperationException("offline"), "test");
}
