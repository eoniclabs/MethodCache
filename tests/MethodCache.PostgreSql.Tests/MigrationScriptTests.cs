using System.Text.RegularExpressions;
using FluentAssertions;

namespace MethodCache.PostgreSql.Tests;

public partial class MigrationScriptTests
{
    private static string Migration => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "migrations", "0001-create-schema.sql"));

    [Fact]
    public void Migration_CreatesTheTablesMethodCacheCreates_ForTheDefaultNames()
    {
        Normalize(Migration).Should().Contain(Normalize(PostgreSqlTables.Script(new PostgreSqlOptions())));
    }

    [Fact]
    public void Migration_RunsInOneTransaction_KeepsValuesInTheRow_AndUsesLz4OnlyWhereTheServerHasIt()
    {
        var sql = Normalize(Migration);

        sql.Should().StartWith("BEGIN;").And.EndWith("COMMIT;");
        sql.Should().Contain("ALTER TABLE \"public\".\"methodcache\" ALTER COLUMN id SET STORAGE PLAIN, ALTER COLUMN value SET STORAGE MAIN;");
        sql.Should().Contain("'lz4' = ANY(enumvals)");
    }

    // Comments and line breaks don't change the statements
    private static string Normalize(string sql) => Whitespace().Replace(Comment().Replace(sql, ""), " ").Trim();

    [GeneratedRegex(@"--[^\n]*")]
    private static partial Regex Comment();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
