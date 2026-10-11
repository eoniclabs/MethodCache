namespace MethodCache.SqlServer.Tests;

public class AtomicStampValidationTests
{
    [Fact]
    public async Task InvalidArgumentsAndCancelledCall_FailBeforeContactingSqlServer()
    {
        var cache = new SqlServerDistributedCache(new SqlServerOptions { ConnectionString = "Server=invalid.example;Database=unused;Integrated Security=true" });
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.WriteInvalidationStampAsync(null!, 1, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.WriteInvalidationStampAsync(new string('x', 450), 1, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.FromSeconds(-1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.MaxValue));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.FromMinutes(1), cancellation.Token));
    }
}
