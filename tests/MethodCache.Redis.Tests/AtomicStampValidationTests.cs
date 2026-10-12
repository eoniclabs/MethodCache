using Xunit;

namespace MethodCache.Redis.Tests;

public class AtomicStampValidationTests
{
    [Fact]
    public async Task InvalidArgumentsAndCancelledCall_DoNotOpenAConnection()
    {
        var connections = 0;
        using var connection = new RedisConnection(() => { connections++; throw new InvalidOperationException("must not connect"); }, owned: false);
        var cache = new RedisDistributedCache(connection);
        await Assert.ThrowsAsync<ArgumentNullException>(() => cache.WriteInvalidationStampAsync(null!, 1, TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.FromSeconds(-1)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.WriteInvalidationStampAsync("stamp", 1, TimeSpan.FromMinutes(1), cancellation.Token));
        Assert.Equal(0, connections);
    }
}
