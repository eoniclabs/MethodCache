using CsCheck;
using MethodCache.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Properties;

public class EntryListingPropertyTests
{
    [Fact]
    public void ListEntries_ArbitraryLimitFiltersAndInsertionOrder_MatchesOrdinalModel()
    {
        Gen.Select(Gen.Int, Gen.Int, Gen.Int).Sample(input =>
        {
            var clock = new FakeTimeProvider();
            using var provider = new ServiceCollection().AddSingleton<TimeProvider>(clock)
                .Configure<MethodCacheMemoryOptions>(options => options.EnableBackgroundCleanup = false)
                .AddMethodCache().BuildServiceProvider();
            var cache = provider.GetRequiredService<ICacheManager>();
            var admin = provider.GetRequiredService<ICacheAdministrator>();
            var keys = new[] { "I", "i", "İ", "ı", "ä", "z", "a:1", "a:2", "a:10", "A:1", "_" };
            var rotation = (int)((uint)input.Item1 % (uint)keys.Length);
            for (var i = 0; i < keys.Length; i++)
            {
                var index = (i + rotation) % keys.Length;
                var policy = CacheRuntimePolicy.Empty("list") with
                {
                    Duration = TimeSpan.FromMinutes(index % 3 == 0 ? 1 : 10),
                    Tags = new[] { index % 2 == 0 ? "even" : "odd" }
                };
                cache.GetOrCreateFastAsync(keys[index], "list", () => Task.FromResult(index), policy).GetAwaiter().GetResult();
            }
            clock.Advance(TimeSpan.FromMinutes(2));
            var prefix = ((uint)input.Item2 % 3) switch { 0 => null, 1 => "a:", _ => "I" };
            var tag = ((uint)input.Item2 % 5) switch { 0 => "even", 1 => "odd", 2 => "missing", _ => null };
            var limit = (int)((uint)input.Item3 % 20) + 1;
            var expected = keys.Where((key, index) => index % 3 != 0
                    && (prefix == null || key.StartsWith(prefix, StringComparison.Ordinal))
                    && (tag == null || tag == (index % 2 == 0 ? "even" : "odd")))
                .OrderBy(key => key, StringComparer.Ordinal).Take(limit).ToArray();
            foreach (var culture in PropertySettings.Cultures)
                Assert.Equal(expected, PropertySettings.InCulture(culture, () => admin.ListEntries(prefix, tag, limit).Select(entry => entry.StoredKey).ToArray()));
            Assert.Equal(keys.Where((_, index) => index % 3 != 0).Count(), admin.ListEntries(limit: int.MaxValue).Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => admin.ListEntries(limit: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => admin.ListEntries(limit: -1));
        }, seed: PropertySettings.Seed, iter: PropertySettings.ModelIterations);
    }
}
