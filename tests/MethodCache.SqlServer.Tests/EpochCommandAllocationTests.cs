using Xunit;

namespace MethodCache.SqlServer.Tests;

public class EpochCommandAllocationTests
{
    [Fact]
    public void RepeatedBatchShape_DoesNotRebuildSqlText()
    {
        var cache = Cache("shape_a");
        _ = cache.EpochCommandText(3);
        const int count = 1000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        string sql = string.Empty;
        for (var i = 0; i < count; i++) sql = cache.EpochCommandText(3);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("[shape_a].[Cache]", sql);
        Assert.True(allocated <= count * 8L, $"Allocated {allocated / (double)count} B per repeated SQL shape.");
    }

    [Fact]
    public void BatchShape_ReadsInitializedVectorWithoutLocks()
    {
        var sql = Cache("shape_a").EpochCommandText(3);
        var read = sql[..sql.IndexOf("IF @@ROWCOUNT = 3 RETURN;", StringComparison.Ordinal)];
        Assert.Contains("(2, @key2)) AS k", read);
        Assert.DoesNotContain("@key3", read);
        Assert.DoesNotContain("UPDLOCK", read);
        Assert.Contains("UPDLOCK", sql);
    }

    [Fact]
    public void BatchShape_ChangesCountAndKeepsProvidersIsolated()
    {
        var a = Cache("shape_a");
        var b = Cache("shape_b");
        var first = a.EpochCommandText(3);
        Assert.Contains("@key2", first);
        Assert.DoesNotContain("@key3", first);
        var larger = a.EpochCommandText(128);
        Assert.Contains("@key127", larger);
        Assert.DoesNotContain("@key128", larger);
        Assert.Equal(first, a.EpochCommandText(3));
        var other = b.EpochCommandText(3);
        Assert.Contains("[shape_b].[Cache]", other);
        Assert.DoesNotContain("[shape_a].[Cache]", other);
        Assert.Throws<ArgumentOutOfRangeException>(() => a.EpochCommandText(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.EpochCommandText(129));
    }

    private static SqlServerDistributedCache Cache(string schema) => new(new SqlServerOptions
    {
        ConnectionString = "Server=localhost;Database=unused;Integrated Security=true;Encrypt=false",
        SchemaName = schema,
        TableName = "Cache"
    });

    [Fact]
    public void ConcurrentBatchShapes_KeepEachRequestedCount()
    {
        var cache = Cache("shape_concurrent");
        Parallel.For(0, 128, i =>
        {
            var count = i % 7 + 1;
            var sql = cache.EpochCommandText(count);
            Assert.Contains($"SELECT {count - 1} AS [Position]", sql);
            Assert.DoesNotContain($"SELECT {count} AS [Position]", sql);
        });
    }
}
