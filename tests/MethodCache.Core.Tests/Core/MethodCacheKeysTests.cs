using System.Globalization;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
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

                // Found by CacheKeyPropertyTests: Enum.ToString("D") used the culture's minus sign (U+2212)
                Assert.Equal("-1", MethodCacheKeys.Part((DayOfWeek)(-1)));
                Assert.Equal("-9223372036854775808", MethodCacheKeys.Part((LongEnum)long.MinValue));
                Assert.Equal("18446744073709551615", MethodCacheKeys.Part((ULongEnum)ulong.MaxValue));
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

        private sealed record Id(string CacheKeyPart) : ICacheKeyProvider;

        private sealed class Money
        {
            private readonly decimal _amount;
            public Money(decimal amount) => _amount = amount;
            public override string ToString() => _amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class Marker
        {
        }

        public enum Status { Active = 1, Closed = 2 }

        public enum LongEnum : long { Min = long.MinValue }

        public enum ULongEnum : ulong { Max = ulong.MaxValue }

        private static readonly CacheRuntimePolicy NoVersion = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);

        public static IEnumerable<object[]> DistinctArgumentLists() => new[]
        {
            // Values that ran into each other when parts were joined with '_'
            new object[] { new object[] { new Id("a_b"), new Id("c") }, new object[] { new Id("a"), new Id("b_c") } },
            new object[] { new object[] { "a", "b" }, new object[] { "a_", "b" } },
            new object[] { new object[] { new Id("x") }, new object[] { "x" } },
            new object[] { new object[] { null!, "x" }, new object[] { "x", null! } },
            new object[] { new object[] { 1 }, new object[] { 1L } },
            new object[] { new object[] { 1 }, new object[] { "1" } },
            // DateTimeOffset was truncated to milliseconds
            new object[] { new object[] { new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }, new object[] { new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(5000) } },
            new object[] { new object[] { TimeSpan.FromTicks(1) }, new object[] { TimeSpan.FromTicks(2) } },
            new object[] { new object[] { (short)1 }, new object[] { (ushort)1 } },
        };

        [Theory]
        [MemberData(nameof(DistinctArgumentLists))]
        public void FastHashKeyGenerator_GivesDifferentArgumentListsDifferentKeys(object[] first, object[] second)
        {
            var generator = new FastHashKeyGenerator();

            Assert.NotEqual(generator.GenerateKey("M", first, NoVersion), generator.GenerateKey("M", second, NoVersion));
        }

        [Fact]
        public void FastHashKeyGenerator_WritesEnumsByNumber()
        {
            // Like generated keys, so renaming a member keeps existing keys; the type still tells enums apart
            var generator = new FastHashKeyGenerator();

            Assert.Equal(generator.GenerateKey("M", new object[] { Status.Closed }, NoVersion), generator.GenerateKey("M", new object[] { (Status)2 }, NoVersion));
            Assert.NotEqual(generator.GenerateKey("M", new object[] { Status.Closed }, NoVersion), generator.GenerateKey("M", new object[] { 2 }, NoVersion));
        }

        [Fact]
        public void KeyGenerators_RefuseObjectsWhoseStateIsNotPublic()
        {
            // Every Money serializes to {}, so Money(1) and Money(1_000_000) would share a cached result
            var fastHash = Assert.Throws<InvalidOperationException>(() => new FastHashKeyGenerator().GenerateKey("M", new object[] { new Money(1) }, NoVersion));
            Assert.Contains(nameof(ICacheKeyProvider), fastHash.Message);
            Assert.Throws<InvalidOperationException>(() => new JsonKeyGenerator().GenerateKey("M", new object[] { new Money(1) }, NoVersion));

            // A type with no state at all is fine
            new FastHashKeyGenerator().GenerateKey("M", new object[] { new Marker() }, NoVersion);
        }

        [Fact]
        public void JsonKeyGenerator_WritesReadableKeysUnderTheMethodPrefix()
        {
            var generator = new JsonKeyGenerator();
            var prefix = MethodCacheKeys.Prefix(typeof(IUserService), "Search");

            var key = generator.GenerateKey(prefix, new object?[] { 42, "alice", null, Status.Closed, new Id("tenant-1"), new[] { 1, 2 } }, NoVersion);

            Assert.Equal(prefix + ":[42,\"alice\",null,2,\"tenant-1\",[1,2]]", key);
        }

        [Fact]
        public void JsonKeyGenerator_HashesLongArgumentLists()
        {
            var generator = new JsonKeyGenerator();
            var longText = new string('x', JsonKeyGenerator.MaxReadableLength);

            var key = generator.GenerateKey("M", new object[] { longText }, NoVersion);
            var other = generator.GenerateKey("M", new object[] { longText + "y" }, NoVersion);

            Assert.Equal("M:".Length + 64, key.Length);
            Assert.NotEqual(key, other);
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
