using Xunit;
using MethodCache.Core;
using MethodCache.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using MethodCache.Core.KeyGenerators;

namespace MethodCache.Core.Tests
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
            var settings = new CacheMethodSettings();

            var key1 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, settings);
            var key2 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, settings);
            var key3 = generator.GenerateKey("Method1", new object[] { 2, "test", true }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void FastHashKeyGenerator_WithPrimitiveArgs_ReturnsDeterministicKey()
        {
            var generator = new FastHashKeyGenerator();
            var settings = new CacheMethodSettings();

            var key1 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, settings);
            var key2 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, settings);
            var key3 = generator.GenerateKey("Method1", new object[] { 2, "test", true }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithPrimitiveArgs_ReturnsDeterministicKey()
        {
            var generator = new JsonKeyGenerator();
            var settings = new CacheMethodSettings();

            var key1 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, settings);
            var key2 = generator.GenerateKey("Method1", new object[] { 1, "test", true }, settings);
            var key3 = generator.GenerateKey("Method1", new object[] { 2, "test", true }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void MessagePackKeyGenerator_WithPlainPOCO_ReturnsDeterministicKey()
        {
            var generator = new MessagePackKeyGenerator();
            var settings = new CacheMethodSettings();

            var obj1 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj2 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj3 = new PlainTestClass { Id = 2, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };

            var key1 = generator.GenerateKey("Method2", new object[] { obj1 }, settings);
            var key2 = generator.GenerateKey("Method2", new object[] { obj2 }, settings);
            var key3 = generator.GenerateKey("Method2", new object[] { obj3 }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void FastHashKeyGenerator_WithPlainPOCO_ReturnsDeterministicKey()
        {
            var generator = new FastHashKeyGenerator();
            var settings = new CacheMethodSettings();

            var obj1 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj2 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj3 = new PlainTestClass { Id = 2, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };

            var key1 = generator.GenerateKey("Method2", new object[] { obj1 }, settings);
            var key2 = generator.GenerateKey("Method2", new object[] { obj2 }, settings);
            var key3 = generator.GenerateKey("Method2", new object[] { obj3 }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithPlainPOCO_ReturnsDeterministicKey()
        {
            var generator = new JsonKeyGenerator();
            var settings = new CacheMethodSettings();

            var obj1 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj2 = new PlainTestClass { Id = 1, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };
            var obj3 = new PlainTestClass { Id = 2, Name = "Test", CreatedAt = new DateTime(2023, 1, 1), IsActive = true };

            var key1 = generator.GenerateKey("Method2", new object[] { obj1 }, settings);
            var key2 = generator.GenerateKey("Method2", new object[] { obj2 }, settings);
            var key3 = generator.GenerateKey("Method2", new object[] { obj3 }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void MessagePackKeyGenerator_WithICacheKeyProvider_UsesCacheKeyPart()
        {
            var generator = new MessagePackKeyGenerator();
            var settings = new CacheMethodSettings();

            var user1 = new TestUser { Id = 1 };
            var user2 = new TestUser { Id = 1 };
            var user3 = new TestUser { Id = 2 };

            var key1 = generator.GenerateKey("Method3", new object[] { user1 }, settings);
            var key2 = generator.GenerateKey("Method3", new object[] { user2 }, settings);
            var key3 = generator.GenerateKey("Method3", new object[] { user3 }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void FastHashKeyGenerator_WithICacheKeyProvider_UsesCacheKeyPart()
        {
            var generator = new FastHashKeyGenerator();
            var settings = new CacheMethodSettings();

            var user1 = new TestUser { Id = 1 };
            var user2 = new TestUser { Id = 1 };
            var user3 = new TestUser { Id = 2 };

            var key1 = generator.GenerateKey("Method3", new object[] { user1 }, settings);
            var key2 = generator.GenerateKey("Method3", new object[] { user2 }, settings);
            var key3 = generator.GenerateKey("Method3", new object[] { user3 }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithICacheKeyProvider_UsesCacheKeyPart()
        {
            var generator = new JsonKeyGenerator();
            var settings = new CacheMethodSettings();

            var user1 = new TestUser { Id = 1 };
            var user2 = new TestUser { Id = 1 };
            var user3 = new TestUser { Id = 2 };

            var key1 = generator.GenerateKey("Method3", new object[] { user1 }, settings);
            var key2 = generator.GenerateKey("Method3", new object[] { user2 }, settings);
            var key3 = generator.GenerateKey("Method3", new object[] { user3 }, settings);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
        }

        [Fact]
        public void MessagePackKeyGenerator_WithVersion_IncludesVersionInKey()
        {
            var generator = new MessagePackKeyGenerator();
            var settings1 = new CacheMethodSettings { Version = 1 };
            var settings2 = new CacheMethodSettings { Version = 2 };

            var key1 = generator.GenerateKey("Method4", new object[] { 1 }, settings1);
            var key2 = generator.GenerateKey("Method4", new object[] { 1 }, settings1);
            var key3 = generator.GenerateKey("Method4", new object[] { 1 }, settings2);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3); // Version is hashed into the key, so different versions produce different keys
        }

        [Fact]
        public void FastHashKeyGenerator_WithVersion_IncludesVersionInKey()
        {
            var generator = new FastHashKeyGenerator();
            var settings1 = new CacheMethodSettings { Version = 1 };
            var settings2 = new CacheMethodSettings { Version = 2 };

            var key1 = generator.GenerateKey("Method4", new object[] { 1 }, settings1);
            var key2 = generator.GenerateKey("Method4", new object[] { 1 }, settings1);
            var key3 = generator.GenerateKey("Method4", new object[] { 1 }, settings2);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
            Assert.Contains("_v1", key1);
            Assert.Contains("_v2", key3);
        }

        [Fact]
        public void JsonKeyGenerator_WithVersion_IncludesVersionInKey()
        {
            var generator = new JsonKeyGenerator();
            var settings1 = new CacheMethodSettings { Version = 1 };
            var settings2 = new CacheMethodSettings { Version = 2 };

            var key1 = generator.GenerateKey("Method4", new object[] { 1 }, settings1);
            var key2 = generator.GenerateKey("Method4", new object[] { 1 }, settings1);
            var key3 = generator.GenerateKey("Method4", new object[] { 1 }, settings2);

            Assert.Equal(key1, key2);
            Assert.NotEqual(key1, key3);
            Assert.Contains("_v1", key1);
            Assert.Contains("_v2", key3);
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

            var settings = new CacheMethodSettings();
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
                var key1 = generator.GenerateKey("ComplexMethod", new object[] { complexObj }, settings);
                var key2 = generator.GenerateKey("ComplexMethod", new object[] { complexObj }, settings);

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

            var settings = new CacheMethodSettings();
            var order = new OrderClass
            {
                OrderId = 123,
                CustomerName = "John Doe",
                Amount = 99.99m,
                Items = new List<string> { "Item1", "Item2", "Item3" }
            };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("ProcessOrder", new object[] { order }, settings);
                var key2 = generator.GenerateKey("ProcessOrder", new object[] { order }, settings);

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

            var settings = new CacheMethodSettings();
            var objWithNulls = new PlainTestClass
            {
                Id = 1,
                Name = null, // Null string
                CreatedAt = DateTime.MinValue,
                IsActive = false
            };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("MethodWithNulls", new object[] { objWithNulls }, settings);
                var key2 = generator.GenerateKey("MethodWithNulls", new object[] { objWithNulls }, settings);

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

            var settings = new CacheMethodSettings();
            var plainObj = new PlainTestClass { Id = 1, Name = "Test" };
            var userObj = new TestUser { Id = 1, Email = "test@example.com" };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("MixedMethod", new object[] { 1, "test", plainObj }, settings);
                var key2 = generator.GenerateKey("MixedMethod", new object[] { 1, "test", userObj }, settings);
                var key3 = generator.GenerateKey("MixedMethod", new object[] { 2, "test", plainObj }, settings);

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

            var settings = new CacheMethodSettings();
            var emptyOrder = new OrderClass
            {
                OrderId = 0,
                CustomerName = "",
                Amount = 0m,
                Items = new List<string>() // Empty list
            };

            foreach (var generator in generators)
            {
                var key1 = generator.GenerateKey("EmptyCollectionMethod", new object[] { emptyOrder }, settings);
                var key2 = generator.GenerateKey("EmptyCollectionMethod", new object[] { emptyOrder }, settings);

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

            var settings = new CacheMethodSettings();

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
                var key1 = generator.GenerateKey("LargeObjectMethod", new object[] { largeOrder }, settings);
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

            var settings = new CacheMethodSettings();
            var testObj = new PlainTestClass { Id = 1, Name = "Test" };

            var messagePackKey = messagePackGenerator.GenerateKey("TestMethod", new object[] { testObj }, settings);
            var fastHashKey = fastHashGenerator.GenerateKey("TestMethod", new object[] { testObj }, settings);
            var jsonKey = jsonGenerator.GenerateKey("TestMethod", new object[] { testObj }, settings);

            // All should be valid non-empty strings
            Assert.NotNull(messagePackKey);
            Assert.NotNull(fastHashKey);
            Assert.NotNull(jsonKey);
            Assert.NotEmpty(messagePackKey);
            Assert.NotEmpty(fastHashKey);
            Assert.NotEmpty(jsonKey);

            // Each generator should be consistent with itself
            var messagePackKey2 = messagePackGenerator.GenerateKey("TestMethod", new object[] { testObj }, settings);
            var fastHashKey2 = fastHashGenerator.GenerateKey("TestMethod", new object[] { testObj }, settings);
            var jsonKey2 = jsonGenerator.GenerateKey("TestMethod", new object[] { testObj }, settings);

            Assert.Equal(messagePackKey, messagePackKey2);
            Assert.Equal(fastHashKey, fastHashKey2);
            Assert.Equal(jsonKey, jsonKey2);
        }

        #endregion
    }
}
