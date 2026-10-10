using FluentAssertions;

namespace MethodCache.Providers.SqlServer.Tests;

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
    public void QualifiedNames_QuoteIdentifiers()
    {
        var options = new SqlServerOptions { SchemaName = "cache", TableName = "odd]name", InvalidationsTableName = "Invalidations" };

        options.QualifiedTableName.Should().Be("[cache].[odd]]name]");
        options.QualifiedInvalidationsTableName.Should().Be("[cache].[Invalidations]");
    }
}
