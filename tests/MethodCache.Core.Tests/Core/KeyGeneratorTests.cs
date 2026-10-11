using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using MethodCache.Serialization.MessagePack;
using Xunit;

namespace MethodCache.Core.Tests.Core
{
    public class KeyGeneratorTests
    {
        // Plain POCO class without any special attributes - this is the real-world scenario
        public class PlainTestClass
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public DateTime CreatedAt { get; set; }
            public bool IsActive { get; set; }
        }

        // Another plain class to test complex object graphs
        public class OrderClass
        {
            public int OrderId { get; set; }
            public string? CustomerName { get; set; }
            public decimal Amount { get; set; }
            public List<string>? Items { get; set; }
        }

        // Class implementing ICacheKeyProvider for custom key generation
        private class TestUser : ICacheKeyProvider
        {
            public int Id { get; set; }
            public string? Email { get; set; }
            public string CacheKeyPart => $"user-{Id}";
        }

        private sealed class TestKeyPart : ICacheKeyProvider
        {
            public required string Part { get; init; }
            public string CacheKeyPart => Part;
        }

        // Class with nested objects
        public class ComplexClass
        {
            public int Id { get; set; }
            public PlainTestClass? NestedObject { get; set; }
            public Dictionary<string, object>? Properties { get; set; }
        }

        [Fact]
        public void MessagePackKeyGenerator_WithPrimitiveArgs_ReturnsDeterministicKey()
        {
            var generator = new MessagePackKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method1", CachePolicy.Empty, CachePolicyFields.None);

            var key1 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, descriptor);
            var key2 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, descriptor);
            var key3 = generator.GenerateKey("Method1", new object[] { 2, "test", true }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void FastHashKeyGenerator_WithPrimitiveArgs_ReturnsDeterministicKey()
        {
            var generator = new FastHashKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method1", CachePolicy.Empty, CachePolicyFields.None);

            var key1 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, descriptor);
            var key2 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, descriptor);
            var key3 = generator.GenerateKey("Method1", new object[] { 2, "test", true }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithPrimitiveArgs_ReturnsDeterministicKey()
        {
            var generator = new JsonKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method1", CachePolicy.Empty, CachePolicyFields.None);

            var key1 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, descriptor);
            var key2 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, descriptor);
            var key3 = generator.GenerateKey("Method1", new object[] { 2, "test", true }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void MessagePackKeyGenerator_WithPlainPOCO_ReturnsDeterministicKey()
        {
            var generator = new MessagePackKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method2", CachePolicy.Empty, CachePolicyFields.None);

            var obj1 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj2 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj3 = new PlainTestClass { Id = 2, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };

            var key1 = generator.GenerateKey("Method2", new object[] { obj1 }, descriptor);
            var key2 = generator.GenerateKey("Method2", new object[] { obj2 }, descriptor);
            var key3 = generator.GenerateKey("Method2", new object[] { obj3 }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void FastHashKeyGenerator_WithPlainPOCO_ReturnsDeterministicKey()
        {
            var generator = new FastHashKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method2", CachePolicy.Empty, CachePolicyFields.None);

            var obj1 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj2 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj3 = new PlainTestClass { Id = 2, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };

            var key1 = generator.GenerateKey("Method2", new object[] { obj1 }, descriptor);
            var key2 = generator.GenerateKey("Method2", new object[] { obj2 }, descriptor);
            var key3 = generator.GenerateKey("Method2", new object[] { obj3 }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithPlainPOCO_ReturnsDeterministicKey()
        {
            var generator = new JsonKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method2", CachePolicy.Empty, CachePolicyFields.None);

            var obj1 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj2 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj3 = new PlainTestClass { Id = 2, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };

            var key1 = generator.GenerateKey("Method2", new object[] { obj1 }, descriptor);
            var key2 = generator.GenerateKey("Method2", new object[] { obj2 }, descriptor);
            var key3 = generator.GenerateKey("Method2", new object[] { obj3 }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void MessagePackKeyGenerator_WithICacheKeyProvider_UsesCacheKeyPart()
        {
            var generator = new MessagePackKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method3", CachePolicy.Empty, CachePolicyFields.None);

            var user1 = new TestUser { Id = 1 };
            var user2 = new TestUser { Id = 1 };
            var user3 = new TestUser { Id = 2 };

            var key1 = generator.GenerateKey("Method3", new object[] { user1 }, descriptor);
            var key2 = generator.GenerateKey("Method3", new object[] { user2 }, descriptor);
            var key3 = generator.GenerateKey("Method3", new object[] { user3 }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void MessagePackKeyGenerator_WithMultipleICacheKeyProviderArgs_AvoidsConcatenationCollisions()
        {
            var generator = new MessagePackKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method3", CachePolicy.Empty, CachePolicyFields.None);

            var key1 = generator.GenerateKey(
                "Method3",
                new object[] { new TestKeyPart { Part = "ab" }, new TestKeyPart { Part = "c" } },
                descriptor);
            var key2 = generator.GenerateKey(
                "Method3",
                new object[] { new TestKeyPart { Part = "a" }, new TestKeyPart { Part = "bc" } },
                descriptor);

            Assert.NotEqual(key1, key2);
        }

        [Fact]
        public void FastHashKeyGenerator_WithICacheKeyProvider_UsesCacheKeyPart()
        {
            var generator = new FastHashKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method3", CachePolicy.Empty, CachePolicyFields.None);

            var user1 = new TestUser { Id = 1 };
            var user2 = new TestUser { Id = 1 };
            var user3 = new TestUser { Id = 2 };

            var key1 = generator.GenerateKey("Method3", new object[] { user1 }, descriptor);
            var key2 = generator.GenerateKey("Method3", new object[] { user2 }, descriptor);
            var key3 = generator.GenerateKey("Method3", new object[] { user3 }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithICacheKeyProvider_UsesCacheKeyPart()
        {
            var generator = new JsonKeyGenerator();
            var descriptor = CacheRuntimePolicy.FromPolicy("Method3", CachePolicy.Empty, CachePolicyFields.None);

            var user1 = new TestUser { Id = 1 };
            var user2 = new TestUser { Id = 1 };
            var user3 = new TestUser { Id = 2 };

            var key1 = generator.GenerateKey("Method3", new object[] { user1 }, descriptor);
            var key2 = generator.GenerateKey("Method3", new object[] { user2 }, descriptor);
            var key3 = generator.GenerateKey("Method3", new object[] { user3 }, descriptor);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void MessagePackKeyGenerator_WithVersion_IncludesVersionInKey()
        {
            var generator = new MessagePackKeyGenerator();
            var policy1 = CachePolicy.Empty with { Version = 1 };
            var policy2 = CachePolicy.Empty with { Version = 2 };
            var descriptor1 = CacheRuntimePolicy.FromPolicy("Method4", policy1, CachePolicyFields.Version);
            var descriptor2 = CacheRuntimePolicy.FromPolicy("Method4", policy2, CachePolicyFields.Version);

            var key1 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor1);
            var key2 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor1);
            var key3 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor2);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3); // Version is hashed into the key, so different versions produce different keys
        }

        [Fact]
        public void MessagePackKeyGenerator_KeysStartWithTheMethodAndVersion()
        {
            var generator = new MessagePackKeyGenerator();
            var versioned = CacheRuntimePolicy.FromPolicy("App.IUsers.Find", CachePolicy.Empty with { Version = 3 }, CachePolicyFields.Version);

            generator.GenerateKey("App.IUsers.Find", new object?[] { 1, null }, CacheRuntimePolicy.Empty("App.IUsers.Find"))
                .Should().MatchRegex("^App\\.IUsers\\.Find:[0-9a-f]{64}$");
            generator.GenerateKey("App.IUsers.Find", new object?[] { 1 }, versioned)
                .Should().MatchRegex("^App\\.IUsers\\.Find:v3:[0-9a-f]{64}$");
        }

        [Fact]
        public void FastHashKeyGenerator_WithVersion_IncludesVersionInKey()
        {
            var generator = new FastHashKeyGenerator();
            var policy1 = CachePolicy.Empty with { Version = 1 };
            var policy2 = CachePolicy.Empty with { Version = 2 };
            var descriptor1 = CacheRuntimePolicy.FromPolicy("Method4", policy1, CachePolicyFields.Version);
            var descriptor2 = CacheRuntimePolicy.FromPolicy("Method4", policy2, CachePolicyFields.Version);

            var key1 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor1);
            var key2 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor1);
            var key3 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor2);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
            Assert.StartsWith("Method4:v1:", key1);
            Assert.StartsWith("Method4:v2:", key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithVersion_IncludesVersionInKey()
        {
            var generator = new JsonKeyGenerator();
            var policy1 = CachePolicy.Empty with { Version = 1 };
            var policy2 = CachePolicy.Empty with { Version = 2 };
            var descriptor1 = CacheRuntimePolicy.FromPolicy("Method4", policy1, CachePolicyFields.Version);
            var descriptor2 = CacheRuntimePolicy.FromPolicy("Method4", policy2, CachePolicyFields.Version);

            var key1 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor1);
            var key2 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor1);
            var key3 = generator.GenerateKey("Method4", new object[] { 1 }, descriptor2);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
            Assert.StartsWith("Method4:v1:[1]:", key1);
            Assert.StartsWith("Method4:v2:[1]:", key3);
        }

        #region Additional Real-World Scenario Tests

        [Fact]
        public void AllKeyGenerators_WithComplexObjectGraphs_ShouldHandleGracefully()
        {
            var generators = new ICacheKeyGenerator[]
            {
                new MessagePackKeyGenerator(),
                new FastHashKeyGenerator(),
                new JsonKeyGenerator()
            };

            var descriptor = CacheRuntimePolicy.FromPolicy("ComplexMethod", CachePolicy.Empty, CachePolicyFields.None);
            var complexObj = new ComplexClass
            {
                Id = 1,
                NestedObject = new PlainTestClass { Id = 2, Name = "Nested", CreatedAt = DateTime.Now, IsActive = true },
                Properties = new Dictionary<string, object>
                {
                    { "key1", "value1" },
                    { "key2", 42 },
                    { "key3", true }
                }
            };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("ComplexMethod", new object[] { complexObj }, descriptor);
                var key2 = generator.GenerateKey("ComplexMethod", new object[] { complexObj }, descriptor);

                Assert.Equal(key1, key2);
                Assert.NotNull(key1);
                Assert.NotEmpty(key1);
            }
        }

        [Fact]
        public void AllKeyGenerators_WithCollections_ShouldHandleDifferentCollectionTypes()
        {
            var generators = new ICacheKeyGenerator[]
            {
                new MessagePackKeyGenerator(),
                new FastHashKeyGenerator(),
                new JsonKeyGenerator()
            };

            var descriptor = CacheRuntimePolicy.FromPolicy("ProcessOrder", CachePolicy.Empty, CachePolicyFields.None);
            var order = new OrderClass
            {
                OrderId = 123,
                CustomerName = "John Doe",
                Amount = 99.99m,
                Items = new List<string> { "Item1", "Item2", "Item3" }
            };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("ProcessOrder", new object[] { order }, descriptor);
                var key2 = generator.GenerateKey("ProcessOrder", new object[] { order }, descriptor);

                Assert.Equal(key1, key2);
                Assert.NotNull(key1);
                Assert.NotEmpty(key1);
            }
        }

        [Fact]
        public void AllKeyGenerators_WithNullValues_ShouldHandleGracefully()
        {
            var generators = new ICacheKeyGenerator[]
            {
                new MessagePackKeyGenerator(),
                new FastHashKeyGenerator(),
                new JsonKeyGenerator()
            };

            var descriptor = CacheRuntimePolicy.FromPolicy("MethodWithNulls", CachePolicy.Empty, CachePolicyFields.None);
            var objWithNulls = new PlainTestClass
            {
                Id = 1,
                Name = null, // Null string
                CreatedAt = DateTime.MinValue,
                IsActive = false
            };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("MethodWithNulls", new object[] { objWithNulls }, descriptor);
                var key2 = generator.GenerateKey("MethodWithNulls", new object[] { objWithNulls }, descriptor);

                Assert.Equal(key1, key2);
                Assert.NotNull(key1);
                Assert.NotEmpty(key1);
            }
        }

        [Fact]
        public void AllKeyGenerators_WithMixedArgumentTypes_ShouldProduceDifferentKeys()
        {
            var generators = new ICacheKeyGenerator[]
            {
                new MessagePackKeyGenerator(),
                new FastHashKeyGenerator(),
                new JsonKeyGenerator()
            };

            var descriptor = CacheRuntimePolicy.FromPolicy("MixedMethod", CachePolicy.Empty, CachePolicyFields.None);
            var plainObj = new PlainTestClass { Id = 1, Name = "Test" };
            var userObj = new TestUser { Id = 1, Email = "test@example.com" };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("MixedMethod", new object[] { 1, "test", plainObj }, descriptor);
                var key2 = generator.GenerateKey("MixedMethod", new object[] { 1, "test", userObj }, descriptor);
                var key3 = generator.GenerateKey("MixedMethod", new object[] { 2, "test", plainObj }, descriptor);

                Assert.NotEqual(key1, key2); // Different object types should produce different keys
                Assert.NotEqual(key1, key3); // Different primitive values should produce different keys
                Assert.NotNull(key1);
                Assert.NotNull(key2);
                Assert.NotNull(key3);
            }
        }

        [Fact]
        public void AllKeyGenerators_WithEmptyCollections_ShouldHandleCorrectly()
        {
            var generators = new ICacheKeyGenerator[]
            {
                new MessagePackKeyGenerator(),
                new FastHashKeyGenerator(),
                new JsonKeyGenerator()
            };

            var descriptor = CacheRuntimePolicy.FromPolicy("EmptyCollectionMethod", CachePolicy.Empty, CachePolicyFields.None);
            var emptyOrder = new OrderClass
            {
                OrderId = 0,
                CustomerName = "",
                Amount = 0m,
                Items = new List<string>() // Empty list
            };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("EmptyCollectionMethod", new object[] { emptyOrder }, descriptor);
                var key2 = generator.GenerateKey("EmptyCollectionMethod", new object[] { emptyOrder }, descriptor);

                Assert.Equal(key1, key2);
                Assert.NotNull(key1);
                Assert.NotEmpty(key1);
            }
        }

        [Fact]
        public void AllKeyGenerators_WithLargeObjects_ShouldPerformReasonably()
        {
            var generators = new ICacheKeyGenerator[]
            {
                new MessagePackKeyGenerator(),
                new FastHashKeyGenerator(),
                new JsonKeyGenerator()
            };

            var descriptor = CacheRuntimePolicy.FromPolicy("LargeObjectMethod", CachePolicy.Empty, CachePolicyFields.None);

            // Create a large object with many properties
            var largeOrder = new OrderClass
            {
                OrderId = 999999,
                CustomerName = new string('A', 1000), // Large string
                Amount = decimal.MaxValue,
                Items = Enumerable.Range(1, 100).Select(i => $"Item{i}").ToList() // 100 items
            };

            foreach (var generator in generators)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var key1 = generator.GenerateKey("LargeObjectMethod", new object[] { largeOrder }, descriptor);
                stopwatch.Stop();

                Assert.NotNull(key1);
                Assert.NotEmpty(key1);
                Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"{generator.GetType().Name} took too long: {stopwatch.ElapsedMilliseconds}ms");
            }
        }

        [Fact]
        public void KeyGenerators_ShouldProduceValidKeysForSameInput()
        {
            var messagePackGenerator = new MessagePackKeyGenerator();
            var fastHashGenerator = new FastHashKeyGenerator();
            var jsonGenerator = new JsonKeyGenerator();

            var descriptor = CacheRuntimePolicy.FromPolicy("TestMethod", CachePolicy.Empty, CachePolicyFields.None);
            var testObj = new PlainTestClass { Id = 1, Name = "Test" };

            var messagePackKey = messagePackGenerator.GenerateKey("TestMethod", new object[] { testObj }, descriptor);
            var fastHashKey = fastHashGenerator.GenerateKey("TestMethod", new object[] { testObj }, descriptor);
            var jsonKey = jsonGenerator.GenerateKey("TestMethod", new object[] { testObj }, descriptor);

            // All should be valid non-empty strings
            Assert.NotNull(messagePackKey);
            Assert.NotNull(fastHashKey);
            Assert.NotNull(jsonKey);
            Assert.NotEmpty(messagePackKey);
            Assert.NotEmpty(fastHashKey);
            Assert.NotEmpty(jsonKey);

            // Each generator should be consistent with itself
            var messagePackKey2 = messagePackGenerator.GenerateKey("TestMethod", new object[] { testObj }, descriptor);
            var fastHashKey2 = fastHashGenerator.GenerateKey("TestMethod", new object[] { testObj }, descriptor);
            var jsonKey2 = jsonGenerator.GenerateKey("TestMethod", new object[] { testObj }, descriptor);

            Assert.Equal(messagePackKey, messagePackKey2);
            Assert.Equal(fastHashKey, fastHashKey2);
            Assert.Equal(jsonKey, jsonKey2);
        }

        #endregion

        public enum Balance { Overdrawn = -1, Zero = 0 }

        [Fact]
        public void FastHashKeyGenerator_NegativeEnum_GetsTheSameKeyInEveryCulture()
        {
            // Found by CacheKeyPropertyTests: Enum.ToString("D") writes -1 as "−1" under sv-SE
            var generator = new FastHashKeyGenerator();
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);
            var invariant = generator.GenerateKey("M", new object[] { Balance.Overdrawn }, policy);

            var original = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
                Assert.Equal(invariant, generator.GenerateKey("M", new object[] { Balance.Overdrawn }, policy));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void FastHashKeyGenerator_LoneSurrogates_GetDistinctKeys()
        {
            // Found by CacheKeyPropertyTests: UTF-8 encoding turned both halves of a surrogate pair into U+FFFD
            var generator = new FastHashKeyGenerator();
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);

            var keys = new[] { "\uD834", "\uDD1E", "�", "𝄞" }
                .Select(s => generator.GenerateKey("M", new object[] { s }, policy))
                .ToArray();

            Assert.Equal(keys.Length, keys.Distinct().Count());
        }

        public sealed record Glyph(char Value, string Name);

        [Theory]
        [InlineData(typeof(FastHashKeyGenerator))]
        [InlineData(typeof(JsonKeyGenerator))]
        public void KeyGenerators_LoneSurrogatesInsideObjects_GetDistinctKeys(Type generatorType)
        {
            // Found by the random-seed run of CacheKeyPropertyTests: JSON writes every lone surrogate as U+FFFD, so
            // lists, dictionary keys and records holding different ones shared a key
            var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);
            string[] texts = ["\uD834", "\uDD1E", "\uFFFD", "\uFFFFd834", "\uFFFF"];

            var args = texts.Select(t => (object)new List<string> { t })
                .Concat(texts.Select(t => (object)new Dictionary<string, int> { [t] = 1 }))
                .Concat(new object[] { new Glyph('\uD834', ""), new Glyph('\uDD1E', ""), new Glyph('\uFFFD', ""), new Glyph('a', "\uD834") })
                .ToArray();
            var keys = args.Select(a => generator.GenerateKey("M", new[] { a }, policy)).ToArray();

            Assert.Equal(keys.Length, keys.Distinct().Count());
        }
    }
}
