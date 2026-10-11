using System.Buffers.Binary;
using MethodCache.Extensibility;
using Xunit;

namespace MethodCache.TestSupport;

internal static class InvalidationStampContract
{
    internal static async Task AssertMaximumAsync(IAtomicInvalidationStore first, IAtomicInvalidationStore second, string key)
    {
        long[] values = [long.MinValue, long.MinValue + 1, -2, -1, 0, 1, 9_007_199_254_740_993L, 9_007_199_254_740_994L, long.MaxValue];
        try
        {
            foreach (var value in values)
            {
                await first.WriteInvalidationStampAsync(key, value, TimeSpan.FromMinutes(10));
                Assert.Equal(value, Read(Assert.IsType<byte[]>(await second.GetAsync(key))));
            }
            foreach (var value in values.Reverse())
            {
                await second.WriteInvalidationStampAsync(key, value, TimeSpan.FromMinutes(1));
                Assert.Equal(long.MaxValue, Read(Assert.IsType<byte[]>(await first.GetAsync(key))));
            }
        }
        finally { await first.RemoveAsync(key); }
    }

    internal static async Task AssertConcurrentMaximumAsync(IAtomicInvalidationStore first, IAtomicInvalidationStore second, string key)
    {
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 16).Select(i => (i % 2 == 0 ? first : second)
                .WriteInvalidationStampAsync(key, (i * 7) % 16, TimeSpan.FromMinutes(10))));
            Assert.Equal(15, Read(Assert.IsType<byte[]>(await first.GetAsync(key))));
            // A delayed earlier operation must not undo the maximum after the concurrent burst.
            await second.WriteInvalidationStampAsync(key, -1, TimeSpan.FromMinutes(1));
            Assert.Equal(15, Read(Assert.IsType<byte[]>(await second.GetAsync(key))));
        }
        finally { await first.RemoveAsync(key); }
    }

    internal static long Read(byte[] bytes)
    {
        Assert.Equal(8, bytes.Length);
        return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }
}
