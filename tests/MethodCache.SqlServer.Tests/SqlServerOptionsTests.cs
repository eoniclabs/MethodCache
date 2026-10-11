using FluentAssertions;

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
    public async Task Startup_DoesNotFailOrWaitLong_WhenTheDatabaseIsUnreachable()
    {
        var options = new SqlServerOptions { ConnectionString = "Server=127.0.0.1,1;Connect Timeout=30;TrustServerCertificate=true" };
        var tables = new SqlServerTables(options);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await tables.StartAsync(CancellationToken.None);

        started.Elapsed.Should().BeLessThan(SqlServerTables.StartupWait + TimeSpan.FromSeconds(2));
        await tables.StopAsync(CancellationToken.None);
        tables.Dispose();
    }

    [Fact]
    public async Task DisposeWithoutStop_StopsTheRetries()
    {
        var options = new SqlServerOptions { ConnectionString = "Server=127.0.0.1,1;Connect Timeout=1;TrustServerCertificate=true" };
        var tables = new SqlServerTables(options);
        await tables.StartAsync(CancellationToken.None);

        tables.Dispose();

        await tables.Creating!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void QualifiedNames_QuoteIdentifiers()
    {
        var options = new SqlServerOptions { SchemaName = "cache", TableName = "odd]name", InvalidationsTableName = "Invalidations" };

        options.QualifiedTableName.Should().Be("[cache].[odd]]name]");
        options.QualifiedInvalidationsTableName.Should().Be("[cache].[Invalidations]");
    }
}
