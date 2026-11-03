using FluentAssertions;

namespace MethodCache.PostgreSql.Tests;

public class PostgreSqlOptionsTests
{
    private const string Connection = "Host=localhost;Database=cache";

    [Fact]
    public void Validate_RequiresConnectionString()
    {
        var act = () => new PostgreSqlOptions().Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*ConnectionString*");
    }

    [Fact]
    public void Validate_AcceptsAConnectionStringFactoryInstead()
    {
        var options = new PostgreSqlOptions { ConnectionStringFactory = _ => Connection };

        options.Validate();
    }

    [Theory]
    [InlineData("", "methodcache", "invalidations")]
    [InlineData("public", " ", "invalidations")]
    [InlineData("public", "methodcache", "")]
    public void Validate_RequiresNames(string schema, string table, string invalidations)
    {
        var options = new PostgreSqlOptions { ConnectionString = Connection, SchemaName = schema, TableName = table, InvalidationsTableName = invalidations };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RejectsNulInNames()
    {
        var options = new PostgreSqlOptions { ConnectionString = Connection, TableName = "cache\0" };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*NUL*");
    }

    [Theory]
    [InlineData("methodcache", "methodcache")]
    [InlineData("cache", "cache_schema")]
    [InlineData("cache_cursor", "cache")]
    [InlineData("cache", "cache_expires")]
    [InlineData("invalidations_created", "invalidations")]
    public void Validate_RejectsTablesOrIndexesWithTheSameName(string table, string invalidations)
    {
        var options = new PostgreSqlOptions { ConnectionString = Connection, TableName = table, InvalidationsTableName = invalidations };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*distinct names*");
    }

    [Fact]
    public void Validate_NamesDifferingOnlyInCase_AreDistinct()
    {
        // Quoted identifiers keep their case, so these are two tables
        var options = new PostgreSqlOptions { ConnectionString = Connection, TableName = "Cache", InvalidationsTableName = "cache" };

        options.Validate();
    }

    [Fact]
    public void Validate_NamesMustFitPostgreSqlIdentifiersWithTheirSuffixes()
    {
        // The longest suffix, _expires and _created, is eight bytes
        new PostgreSqlOptions { ConnectionString = Connection, TableName = new string('t', 55) }.Validate();
        new PostgreSqlOptions { ConnectionString = Connection, InvalidationsTableName = new string('i', 55) }.Validate();
        new PostgreSqlOptions { ConnectionString = Connection, SchemaName = new string('s', 63) }.Validate();

        var table = () => new PostgreSqlOptions { ConnectionString = Connection, TableName = new string('t', 56) }.Validate();
        var invalidations = () => new PostgreSqlOptions { ConnectionString = Connection, InvalidationsTableName = new string('i', 56) }.Validate();
        var schema = () => new PostgreSqlOptions { ConnectionString = Connection, SchemaName = new string('s', 64) }.Validate();
        var bytes = () => new PostgreSqlOptions { ConnectionString = Connection, TableName = new string('é', 28) }.Validate();

        table.Should().Throw<InvalidOperationException>().WithMessage("*63 bytes*");
        invalidations.Should().Throw<InvalidOperationException>();
        schema.Should().Throw<InvalidOperationException>();
        bytes.Should().Throw<InvalidOperationException>("é takes two bytes in UTF-8");
    }

    [Fact]
    public void Validate_RequiresPositiveBackplaneTimes()
    {
        var polling = () => new PostgreSqlOptions { ConnectionString = Connection, BackplanePollingInterval = TimeSpan.Zero }.Validate();
        var retention = () => new PostgreSqlOptions { ConnectionString = Connection, BackplaneRetention = TimeSpan.Zero }.Validate();

        polling.Should().Throw<InvalidOperationException>();
        retention.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Validate_RequiresAtLeastOneMinuteOfRetention()
    {
        // Instances clear their memory after an outage of half the retention, so a short one would clear constantly
        var act = () => new PostgreSqlOptions { ConnectionString = Connection, BackplaneRetention = TimeSpan.FromSeconds(30) }.Validate();

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one minute*");
    }

    [Fact]
    public void QualifiedNames_QuoteIdentifiers()
    {
        var options = new PostgreSqlOptions { SchemaName = "Cache", TableName = "odd\"name", InvalidationsTableName = "Invalidations" };

        options.QualifiedTableName.Should().Be("\"Cache\".\"odd\"\"name\"");
        options.QualifiedVersionTableName.Should().Be("\"Cache\".\"odd\"\"name_schema\"");
        options.QualifiedInvalidationsTableName.Should().Be("\"Cache\".\"Invalidations\"");
        options.QualifiedCursorTableName.Should().Be("\"Cache\".\"Invalidations_cursor\"");
    }

    [Fact]
    public void Defaults_MatchTheMigrationScript()
    {
        var options = new PostgreSqlOptions();

        options.SchemaName.Should().Be("public");
        options.TableName.Should().Be("methodcache");
        options.InvalidationsTableName.Should().Be("methodcache_invalidations");
        options.CreateTablesIfMissing.Should().BeTrue();
        options.EnableBackplane.Should().BeTrue();
        options.BackplanePollingInterval.Should().Be(TimeSpan.FromSeconds(1));
        options.BackplaneRetention.Should().Be(TimeSpan.FromHours(1));
    }
}
