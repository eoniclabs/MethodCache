using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.Tests;

public class PublishCancellationReviewTests
{
    [Fact]
    public async Task Publish_CancelledWhileOpeningConnection_StopsWaiting()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        var opening = new TaskCompletionSource<IConnectionMultiplexer>();
        await using var backplane = new RedisBackplane(() => opening.Task, "review");
        using var cancellation = new CancellationTokenSource();
        var publication = backplane.PublishInvalidationAsync("key", cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication.WaitAsync(TimeSpan.FromSeconds(10)));
            await subscriber.DidNotReceive().PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
        }
        finally
        {
            opening.SetResult(connection);
            try { await publication; } catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_AlreadyCancelled_DoesNotSend(bool tag)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        connection.GetSubscriber().Returns(subscriber);
        await using var backplane = new RedisBackplane(() => Task.FromResult(connection), "review");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tag
            ? backplane.PublishTagInvalidationAsync("tag", cancellation.Token)
            : backplane.PublishInvalidationAsync("key", cancellation.Token));
        await subscriber.DidNotReceive().PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>());
    }
}
