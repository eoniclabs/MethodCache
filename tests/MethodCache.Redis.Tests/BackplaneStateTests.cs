using MethodCache.Extensibility;
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

    private static ConnectionFailedEventArgs Failure(object connection, ConnectionType type)
        => new(connection, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 6379), type,
            ConnectionFailureType.SocketFailure, new InvalidOperationException("offline"), "test");
}
