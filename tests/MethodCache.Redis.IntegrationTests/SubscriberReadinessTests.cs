using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Xunit;

namespace MethodCache.Redis.IntegrationTests;

public class SubscriberReadinessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialSubscriptionHandshakeFailure_RetriesUntilSubscriberAnswers(bool disconnected)
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var failure = disconnected
            ? (Exception)new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Subscription is connecting")
            : new TaskCanceledException("FailFast subscription backlog");
        var ready = RedisFixture.WaitForSubscriberAsync(() =>
            ++calls == 1 ? Task.FromException(failure) : Task.CompletedTask, time);

        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromMilliseconds(10));
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConnectedSubscriber_DoesNotDelayOrRetry()
    {
        var calls = 0;
        var ready = RedisFixture.WaitForSubscriberAsync(() => { calls++; return Task.CompletedTask; }, new FakeTimeProvider());
        Assert.True(ready.IsCompletedSuccessfully);
        await ready;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PermanentConnectionFailure_StopsAtDeadline()
    {
        var time = new FakeTimeProvider();
        var failure = new TaskCanceledException("Still connecting");
        var ready = RedisFixture.WaitForSubscriberAsync(() => Task.FromException(failure), time);
        time.Advance(TimeSpan.FromSeconds(20));
        var error = await Assert.ThrowsAsync<TimeoutException>(() => ready.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public async Task HungPing_StopsAtDeadline()
    {
        var time = new FakeTimeProvider();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = RedisFixture.WaitForSubscriberAsync(() => pending.Task, time);
        time.Advance(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => ready.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task NonConnectionFailure_IsNotHiddenByRetries()
    {
        var failure = new InvalidOperationException("Invalid fixture");
        var ready = RedisFixture.WaitForSubscriberAsync(() => Task.FromException(failure), new FakeTimeProvider());
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => ready));
    }
}
