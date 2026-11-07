using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// <see cref="MethodCacheWeigher.Bytes(string, object)"/> weighs an entry in approximate bytes: its key and a string or an array of
/// numbers by size, any other value as <see cref="MethodCacheWeigher.OtherValue"/>, or as the weight given to
/// <see cref="MethodCacheWeigher.Bytes(long)"/>.
/// </summary>
public class MethodCacheWeigherTests
{
    public sealed record Product(string Name);

    [Theory]
    [MemberData(nameof(Values))]
    public void Bytes_WeighsTheKeyAndTheValue(object? value, long valueBytes)
    {
        Assert.Equal(6 + valueBytes, MethodCacheWeigher.Bytes("abc", value));
    }

    public static TheoryData<object?, long> Values() => new()
    {
        { null, 0 },
        { "", 0 },
        { "hello", 10 },
        { new float[384], 384 * 4 },
        { new double[3], 24 },
        { new byte[100], 100 },
        { new bool[3], 3 },
        { new char[5], 10 },
        { new long[2, 3], 48 },
        { new string[2], MethodCacheWeigher.OtherValue },
        { new float[2][], MethodCacheWeigher.OtherValue },
        { new decimal[2], MethodCacheWeigher.OtherValue },
        { 42, MethodCacheWeigher.OtherValue },
        { new Product("p"), MethodCacheWeigher.OtherValue },
    };

    [Fact]
    public void Bytes_WithAWeightForOtherValues_GivesItToValuesItCantMeasure()
    {
        var weigh = MethodCacheWeigher.Bytes(otherValue: 1024);

        Assert.Equal(6 + 1024, weigh("abc", new Product("p")));
        Assert.Equal(6 + 1024, weigh("abc", 42));
    }

    [Fact]
    public void Bytes_WithAWeightForOtherValues_StillMeasuresStringsAndArrays()
    {
        var weigh = MethodCacheWeigher.Bytes(otherValue: 1024);

        Assert.Equal(6 + 10, weigh("abc", "hello"));
        Assert.Equal(6 + 384 * 4, weigh("abc", new float[384]));
        Assert.Equal(6, weigh("abc", null));
    }

    [Fact]
    public void Bytes_WithANegativeWeightForOtherValues_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MethodCacheWeigher.Bytes(otherValue: -1));
    }

    [Fact]
    public async Task Bytes_WithAWeightForOtherValues_BoundsACacheOfRecords()
    {
        await using var provider = new ServiceCollection()
            .AddMethodCache(mc => mc.Memory(o =>
            {
                o.WeightLimit = 10_000;
                o.Weigher = MethodCacheWeigher.Bytes(otherValue: 1024);
            }))
            .BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var administrator = provider.GetRequiredService<ICacheAdministrator>();

        for (var i = 0; i < 20; i++) await cache.SetAsync($"product:{i}", new Product("p"), o => o.WithDuration(TimeSpan.FromMinutes(1)));
        var stats = await administrator.GetStatsAsync();

        // 64 per record would keep all 20 within 10 000; about 1 KB each keeps fewer than 10
        Assert.True(stats.LocalMethodEntries < 10, $"{stats.LocalMethodEntries} entries");
        Assert.True(stats.Evictions > 0);
    }

    [Fact]
    public async Task Bytes_BoundsTheCacheBySize()
    {
        await using var provider = new ServiceCollection()
            .AddMethodCache(mc => mc.Memory(o =>
            {
                o.WeightLimit = 10_000;
                o.Weigher = MethodCacheWeigher.Bytes;
            }))
            .BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheManager>();
        var administrator = provider.GetRequiredService<ICacheAdministrator>();

        for (var i = 0; i < 20; i++) await cache.SetAsync($"embedding:{i}", new float[384], o => o.WithDuration(TimeSpan.FromMinutes(1)));
        var stats = await administrator.GetStatsAsync();

        Assert.True(stats.TotalWeight <= 10_000, $"{stats.TotalWeight}");
        Assert.True(stats.LocalMethodEntries <= 6, $"{stats.LocalMethodEntries} entries");
        Assert.True(stats.Evictions > 0);
    }
}
