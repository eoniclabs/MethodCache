using Xunit;

namespace MethodCache.Providers.SqlServer.IntegrationTests.Tests;

/// <summary>
/// Test collection to ensure SQL Server backplane tests run sequentially.
/// This prevents race conditions and resource contention when multiple tests
/// access the same database tables simultaneously.
/// </summary>
[CollectionDefinition("SqlServerBackplane", DisableParallelization = true)]
public class SqlServerBackplaneTestCollection
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}