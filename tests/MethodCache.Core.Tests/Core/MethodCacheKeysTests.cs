using System.Globalization;
using MethodCache.Abstractions.Policies;
using MethodCache.Core.Runtime.Core;
using MethodCache.Core.Runtime.KeyGeneration;
using Xunit;

namespace MethodCache.Core.Tests.Core
{
    public class MethodCacheKeysTests
    {
        private interface IUserService { }

        private interface IRepository<T> { }

        [Fact]
        public void Prefix_QualifiesTheMethodWithItsServiceType()
        {
            var prefix = MethodCacheKeys.Prefix(typeof(IUserService), "GetUser");

            Assert.Equal("MethodCache.Core.Tests.Core.MethodCacheKeysTests+IUserService.GetUser", prefix);
        }

        [Fact]
        public void Prefix_AddsTheVersionWhenSet()
        {
            Assert.EndsWith(".GetUser:v3", MethodCacheKeys.Prefix(typeof(IUserService), "GetUser", 3));
        }

        [Fact]
        public void Prefix_NamesGenericArgumentsWithoutAssemblyVersions()
        {
            var prefix = MethodCacheKeys.Prefix(typeof(IRepository<int>), "Get");

            Assert.Contains("[System.Int32]", prefix);
            Assert.DoesNotContain("Version=", prefix);
            Assert.NotEqual(prefix, MethodCacheKeys.Prefix(typeof(IRepository<long>), "Get"));
        }

        [Fact]
        public void For_JoinsPartsAfterThePrefix()
        {
            var key = MethodCacheKeys.For(typeof(IUserService), "Find", MethodCacheKeys.Part(42), MethodCacheKeys.Part("abc"));

            Assert.Equal(MethodCacheKeys.Prefix(typeof(IUserService), "Find") + ":42:abc", key);
        }

        [Fact]
        public void Part_DistinguishesNullFromTheStringsThatLookLikeIt()
        {
            var parts = new[]
            {
                MethodCacheKeys.Part((string?)null),
                MethodCacheKeys.Part("null"),
                MethodCacheKeys.Part("\\0"),
                MethodCacheKeys.Part(string.Empty)
            };

            Assert.Equal(parts.Length, parts.Distinct().Count());
        }

        [Fact]
        public void Part_EscapesSeparatorsSoArgumentBoundariesStayUnique()
        {
            var first = MethodCacheKeys.For(typeof(IUserService), "Find", MethodCacheKeys.Part("a:b"), MethodCacheKeys.Part("c"));
            var second = MethodCacheKeys.For(typeof(IUserService), "Find", MethodCacheKeys.Part("a"), MethodCacheKeys.Part("b:c"));

            Assert.NotEqual(first, second);
            Assert.Equal("a\\:b", MethodCacheKeys.Part("a:b"));
            Assert.Equal("a\\\\b", MethodCacheKeys.Part("a\\b"));
        }

        [Fact]
        public void Part_ReturnsPlainStringsUnchanged()
        {
            const string value = "plain-value";

            Assert.Same(value, MethodCacheKeys.Part(value));
        }

        [Fact]
        public void Part_IgnoresTheCurrentCulture()
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("sv-SE");

                Assert.Equal("1.5", MethodCacheKeys.Part(1.5));
                Assert.Equal("1.5", MethodCacheKeys.Part(1.5m));
                Assert.Equal("-3", MethodCacheKeys.Part(-3));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void Part_KeepsSubSecondPrecisionForDates()
        {
            var first = new DateTime(2024, 1, 1, 8, 0, 0, 100, DateTimeKind.Utc);
            var second = new DateTime(2024, 1, 1, 8, 0, 0, 900, DateTimeKind.Utc);

            Assert.NotEqual(MethodCacheKeys.Part(first), MethodCacheKeys.Part(second));
        }

        [Fact]
        public void Part_GivesTheSameKeyForTheSameInstantInLocalAndUtcTime()
        {
            var utc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.Equal(MethodCacheKeys.Part(utc), MethodCacheKeys.Part(utc.ToLocalTime()));
        }

        [Fact]
        public void FastHashKeyGenerator_KeysShareTheMethodPrefix()
        {
            var generator = new FastHashKeyGenerator();
            var prefix = MethodCacheKeys.Prefix(typeof(IUserService), "Search");
            var policy = CacheRuntimePolicy.FromPolicy("Search", CachePolicy.Empty with { Version = 2 }, CachePolicyFields.Version);

            var key = generator.GenerateKey(prefix, new object[] { new[] { 1, 2, 3 } }, policy);

            Assert.StartsWith(prefix + ":v2:", key);
            Assert.Equal(prefix.Length + ":v2:".Length + 32, key.Length);
        }

        [Fact]
        public void FastHashKeyGenerator_GivesDistinctKeysForSimilarStrings()
        {
            // The previous 64-bit FNV variant mapped these 3,844 strings to 256 keys.
            var generator = new FastHashKeyGenerator();
            var policy = CacheRuntimePolicy.FromPolicy("Sku", CachePolicy.Empty, CachePolicyFields.None);
            const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

            var keys = new HashSet<string>();
            var count = 0;
            foreach (var first in alphabet)
            {
                foreach (var second in alphabet)
                {
                    keys.Add(generator.GenerateKey("Sku", new object[] { $"SKU{first}-0000-0{second}-ZZZ" }, policy));
                    count++;
                }
            }

            Assert.Equal(count, keys.Count);
        }

        [Fact]
        public void FastHashKeyGenerator_GivesTheSameKeyForTheSameInstantInLocalAndUtcTime()
        {
            var generator = new FastHashKeyGenerator();
            var policy = CacheRuntimePolicy.FromPolicy("At", CachePolicy.Empty, CachePolicyFields.None);
            var utc = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

            Assert.Equal(
                generator.GenerateKey("At", new object[] { utc }, policy),
                generator.GenerateKey("At", new object[] { utc.ToLocalTime() }, policy));
        }
    }
}
