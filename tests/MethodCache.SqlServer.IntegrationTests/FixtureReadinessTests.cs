using System.Reflection;
using MethodCache.SqlServer;

namespace MethodCache.SqlServer.IntegrationTests;

public class FixtureReadinessTests
{
    [Fact]
    public async Task TableCreationStillPendingAfterHostedStartup_TestWaitsForIt()
    {
        var creation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tables = WithCreation(creation.Task);
        var readiness = SqlServerFixture.WaitForTablesAsync([tables]);
        try { Assert.False(readiness.IsCompleted); }
        finally { creation.TrySetResult(); }
        await readiness;
    }

    [Fact]
    public async Task TableCreationFails_TestSetupReportsTheFailure()
    {
        var error = new InvalidOperationException("table creation failed");
        using var tables = WithCreation(Task.FromException(error));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => SqlServerFixture.WaitForTablesAsync([tables])));
    }

    [Fact]
    public async Task CancelledSetup_DoesNotTreatPendingTablesAsReady()
    {
        var creation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var tables = WithCreation(creation.Task);
        using var cancellation = new CancellationTokenSource();
        var readiness = SqlServerFixture.WaitForTablesAsync([tables], cancellation.Token);
        cancellation.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readiness); }
        finally { creation.TrySetResult(); }
        await SqlServerFixture.WaitForTablesAsync([tables]);
    }

    private static SqlServerTables WithCreation(Task creation)
    {
        // Model StartAsync returning at its bounded startup deadline while actual creation continues.
        var tables = new SqlServerTables(new SqlServerOptions());
        typeof(SqlServerTables).GetField("_creating", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tables, creation);
        return tables;
    }
}
