using Microsoft.Data.SqlClient;
using Xunit;

namespace MethodCache.SqlServer.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public class EpochContractTests(SqlServerFixture fixture)
{
    [SkippableFact]
    public async Task NativeAtomicEpochs_ConformAcrossIndependentInstances()
    {
        var options = new SqlServerOptions
        { ConnectionString = fixture.RequireConnectionString(), SchemaName = "epochs_" + Guid.NewGuid().ToString("N") };
        try
        {
            await SqlServerTables.CreateIfMissingAsync(options, CancellationToken.None);
            await MethodCache.TestSupport.EpochBackendContract.RunAsync(new SqlServerDistributedCache(options), new SqlServerDistributedCache(options), "epochs:");
        }
        finally { await BackplaneIsolationTests.DropSchemaAsync(options); }
    }
}
