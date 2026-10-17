using MethodCache.Extensibility;
using Xunit;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class BackplaneStateTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task PollingState_TracksEstablishedDeliveryFailureAndRecovery()
    {
        var options = new SqlServerOptions { ConnectionString = fixture.RequireConnectionString(), SchemaName = "health_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await using var receiver = new SqlServerBackplane(options);
            await using var sender = new SqlServerBackplane(options);
            Assert.Equal(BackplaneState.Connecting, receiver.ConnectionState);
            await receiver.PollOnceAsync(_ => Task.CompletedTask, CancellationToken.None);
            Assert.Equal(BackplaneState.Connected, receiver.ConnectionState);
            await sender.PublishInvalidationAsync("key");
            await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.PollOnceAsync(_ => throw new InvalidOperationException("delivery failed"), CancellationToken.None));
            Assert.Equal(BackplaneState.Disconnected, receiver.ConnectionState);
            var delivered = 0;
            await receiver.PollOnceAsync(_ => { delivered++; return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal(1, delivered);
            Assert.Equal(BackplaneState.Connected, receiver.ConnectionState);
            await receiver.UnsubscribeAsync();
            Assert.Equal(BackplaneState.Disconnected, receiver.ConnectionState);
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }
}
