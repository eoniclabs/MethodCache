using Microsoft.Extensions.Time.Testing;

namespace MethodCache.PostgreSql.Tests;

public class PostgreSqlTablesTests
{
    [Fact]
    public async Task Startup_PendingTableCreation_ReturnsAtItsDeadline()
    {
        var clock = new FakeTimeProvider();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new PostgreSqlConnection("Host=localhost");
        using var tables = new PostgreSqlTables(connection, new PostgreSqlOptions(),
            createTables: (_, token) => pending.Task.WaitAsync(token), timeProvider: clock);

        var startup = tables.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => tables.Creating != null);
            clock.Advance(PostgreSqlTables.StartupWait - TimeSpan.FromMilliseconds(1));
            Assert.False(startup.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await startup.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(tables.Creating!.IsCompleted);
        }
        finally
        {
            await tables.StopAsync(CancellationToken.None);
            await startup;
        }
    }

    [Fact]
    public async Task Startup_CreationFails_RetriesAtItsInterval()
    {
        var clock = new ObservedFakeTimeProvider();
        var attempts = 0;
        await using var connection = new PostgreSqlConnection("Host=localhost");
        using var tables = new PostgreSqlTables(connection, new PostgreSqlOptions(), createTables: (_, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("database unavailable"))
                : Task.CompletedTask,
            timeProvider: clock);

        var startup = tables.StartAsync(CancellationToken.None);
        await clock.RetryTimerRegistered.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(PostgreSqlTables.StartupWait);
        await startup.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(PostgreSqlTables.RetryInterval - PostgreSqlTables.StartupWait - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Volatile.Read(ref attempts));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await tables.Creating!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, Volatile.Read(ref attempts));
        await tables.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_IncompatibleSchema_FailsStartupWithoutRetrying()
    {
        var attempts = 0;
        await using var connection = new PostgreSqlConnection("Host=localhost");
        using var tables = new PostgreSqlTables(connection, new PostgreSqlOptions(), createTables: (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new MethodCache.Infrastructure.IncompatibleDistributedScopeException("incompatible"));
        });

        // Retrying can't fix it, and serving with an incompatible layout could return wrong values
        await Assert.ThrowsAsync<MethodCache.Infrastructure.IncompatibleDistributedScopeException>(() => tables.StartAsync(CancellationToken.None));
        await Assert.ThrowsAsync<MethodCache.Infrastructure.IncompatibleDistributedScopeException>(() => tables.Creating!);
        Assert.Equal(1, attempts);
        await tables.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DisposeWithoutStop_CancelsPendingTableCreation()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new PostgreSqlConnection("Host=localhost");
        var tables = new PostgreSqlTables(connection, new PostgreSqlOptions(), createTables: (_, token) =>
        {
            began.TrySetResult();
            return pending.Task.WaitAsync(token);
        }, timeProvider: new FakeTimeProvider());

        var startup = tables.StartAsync(CancellationToken.None);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(30));
        tables.Dispose();
        await startup.WaitAsync(TimeSpan.FromSeconds(30));
        await tables.Creating!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    private sealed class ObservedFakeTimeProvider : FakeTimeProvider
    {
        private readonly TaskCompletionSource _retryTimerRegistered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RetryTimerRegistered => _retryTimerRegistered.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == PostgreSqlTables.RetryInterval) _retryTimerRegistered.TrySetResult();
            return timer;
        }
    }
}
