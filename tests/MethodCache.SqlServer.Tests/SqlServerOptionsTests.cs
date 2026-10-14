using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.SqlServer.Tests;

public class SqlServerOptionsTests
{
    [Fact]
    public void Validate_RequiresConnectionString()
    {
        var act = () => new SqlServerOptions().Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionString*");
    }

    [Theory]
    [InlineData("", "MethodCache", "Invalidations")]
    [InlineData("dbo", " ", "Invalidations")]
    [InlineData("dbo", "MethodCache", "")]
    public void Validate_RequiresTableNames(string schema, string table, string invalidations)
    {
        var options = new SqlServerOptions
        {
            ConnectionString = "Server=.",
            SchemaName = schema,
            TableName = table,
            InvalidationsTableName = invalidations
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RequiresPositiveBackplaneTimes()
    {
        var options = new SqlServerOptions { ConnectionString = "Server=.", BackplanePollingInterval = TimeSpan.Zero };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RequiresAtLeastOneMinuteOfRetention()
    {
        // Instances clear their memory after an outage of half the retention, so a short one would clear constantly
        var options = new SqlServerOptions { ConnectionString = "Server=.", BackplaneRetention = TimeSpan.FromSeconds(30) };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Startup_PendingTableCreation_ReturnsAtItsDeadline()
    {
        var clock = new ObservedFakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        services.AddSingleton(sp => new SqlServerTables(new SqlServerOptions(),
            createTables: (_, token) => pending.Task.WaitAsync(token),
            timeProvider: sp.GetRequiredService<TimeProvider>()));
        using var provider = services.BuildServiceProvider();
        var tables = provider.GetRequiredService<SqlServerTables>();
        var startup = tables.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(1, clock.TimersCreated);
            clock.Advance(SqlServerTables.StartupWait - TimeSpan.FromMilliseconds(1));
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
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(sp => new SqlServerTables(new SqlServerOptions(), createTables: (_, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("database unavailable"))
                : Task.CompletedTask,
            timeProvider: sp.GetRequiredService<TimeProvider>()));
        using var provider = services.BuildServiceProvider();
        var tables = provider.GetRequiredService<SqlServerTables>();
        var startup = tables.StartAsync(CancellationToken.None);
        await clock.RetryTimerRegistered.WaitAsync(TimeSpan.FromSeconds(30));
        clock.Advance(SqlServerTables.StartupWait);
        await startup.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref attempts));
        clock.Advance(SqlServerTables.RetryInterval - SqlServerTables.StartupWait - TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Volatile.Read(ref attempts));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await tables.Creating!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, Volatile.Read(ref attempts));
        await tables.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_DoesNotRunTableCreationOnTheCallingThread()
    {
        // SqlClient can block before its first await; if that ran inside StartAsync, StartupWait would not bound it
        var options = new SqlServerOptions { ConnectionString = "Server=127.0.0.1,1" };
        var ranOnCaller = false;
        var tables = new SqlServerTables(options, createTables: (_, _) =>
        {
            ranOnCaller = _insideStartAsync;
            return Task.CompletedTask;
        });

        _insideStartAsync = true;
        var start = tables.StartAsync(CancellationToken.None);
        _insideStartAsync = false;
        await start;
        await tables.Creating!;

        ranOnCaller.Should().BeFalse();
        await tables.StopAsync(CancellationToken.None);
        tables.Dispose();
    }

    [ThreadStatic]
    private static bool _insideStartAsync;

    [Fact]
    public async Task DisposeWithoutStop_CancelsPendingTableCreation()
    {
        var clock = new ObservedFakeTimeProvider();
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tables = new SqlServerTables(new SqlServerOptions(), createTables: (_, token) =>
        {
            began.TrySetResult();
            return pending.Task.WaitAsync(token);
        }, timeProvider: clock);
        var startup = tables.StartAsync(CancellationToken.None);
        await began.Task.WaitAsync(TimeSpan.FromSeconds(30));
        tables.Dispose();
        await startup.WaitAsync(TimeSpan.FromSeconds(30));
        await tables.Creating!.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void QualifiedNames_QuoteIdentifiers()
    {
        var options = new SqlServerOptions { SchemaName = "cache", TableName = "odd]name", InvalidationsTableName = "Invalidations" };

        options.QualifiedTableName.Should().Be("[cache].[odd]]name]");
        options.QualifiedInvalidationsTableName.Should().Be("[cache].[Invalidations]");
    }

    private sealed class ObservedFakeTimeProvider : FakeTimeProvider
    {
        private readonly TaskCompletionSource _retryTimerRegistered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _timersCreated;
        public Task RetryTimerRegistered => _retryTimerRegistered.Task;
        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timersCreated);
            if (dueTime == SqlServerTables.RetryInterval) _retryTimerRegistered.TrySetResult();
            return timer;
        }
    }

}
