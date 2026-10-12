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
                .Concat(new object[] { new object[] { "\uD834" }, new object[] { new[] { 0xD834 } }, new object[] { '\uD834' }, new object[] { 0xD834 } })
                .ToArray();
            var keys = args.Select(a => generator.GenerateKey("M", new[] { a }, policy)).ToArray();

            Assert.Equal(keys.Length, keys.Distinct().Count());
        }

        public sealed record Boxed(object? Value);

        public abstract record Shape(string Name);

        public record Circle(string Name, int Radius) : Shape(Name);

        public sealed record Square(string Name, int Side) : Shape(Name);

        public sealed record ShapeHolder(Shape? Shape);

        public interface IShape { string Name { get; } }

        public sealed record Triangle(string Name, int Base) : IShape;

        public sealed record Hexagon(string Name, int Base) : IShape;

        public sealed record IShapeHolder(IShape? Shape);

        public sealed record CircleHolder(Circle? Circle);

        public sealed record LargeCircle(string Name, int Radius, int Scale) : Circle(Name, Radius);

        public static TheoryData<string, object, object> NestedValuesOfDifferentTypes() => new()
        {
            { "int and long in object[]", new object[] { 1 }, new object[] { 1L } },
            { "int and double in object[]", new object[] { 1 }, new object[] { 1.0 } },
            { "int and decimal in object[]", new object[] { 1 }, new object[] { 1m } },
            { "int and short in object[]", new object[] { 1 }, new object[] { (short)1 } },
            { "int and enum in object[]", new object[] { 1 }, new object[] { DayOfWeek.Monday } },
            { "int and long in List<object>", new List<object> { 1 }, new List<object> { 1L } },
            { "int and double in List<object>", new List<object> { 1 }, new List<object> { 1.0 } },
            { "int and long in an object property", new Boxed(1), new Boxed(1L) },
            { "int and double in an object property", new Boxed(1), new Boxed(1.0) },
            { "int and long in Dictionary<string, object>", new Dictionary<string, object> { ["a"] = 1 }, new Dictionary<string, object> { ["a"] = 1L } },
            { "derived-only property behind a base-typed property", new ShapeHolder(new Circle("a", 1)), new ShapeHolder(new Circle("a", 2)) },
            { "two derived types behind a base-typed property", new ShapeHolder(new Circle("a", 1)), new ShapeHolder(new Square("a", 1)) },
            { "two types behind an interface-typed property", new IShapeHolder(new Triangle("a", 1)), new IShapeHolder(new Hexagon("a", 1)) },
            { "derived-only property behind an interface-typed property", new IShapeHolder(new Triangle("a", 1)), new IShapeHolder(new Triangle("a", 2)) },
            { "a derived record behind its non-sealed base", new CircleHolder(new Circle("a", 1)), new CircleHolder(new LargeCircle("a", 1, 2)) },
            { "base-typed list elements", new List<Shape> { new Circle("a", 1) }, new List<Shape> { new Circle("a", 2) } },
            { "int and long in nested object[]", new object[] { new object[] { 1 } }, new object[] { new object[] { 1L } } },
            { "int[] and long[] in object[]", new object[] { new[] { 1 } }, new object[] { new[] { 1L } } },
            { "int[] and List<int> in object[]", new object[] { new[] { 1 } }, new object[] { new List<int> { 1 } } },
            { "nint and long in object[]", new object[] { (nint)1 }, new object[] { 1L } },
            { "nint and nuint in object[]", new object[] { (nint)1 }, new object[] { (nuint)1 } },
            { "nuint and ulong in List<object>", new List<object> { (nuint)1 }, new List<object> { 1UL } },
            { "nint and int in an object property", new Boxed((nint)1), new Boxed(1) },
            { "nint[] and long[] in object[]", new object[] { new nint[] { 1 } }, new object[] { new[] { 1L } } },
        };

        public sealed record NativeHandle(nint Handle, nuint Size);

        public static TheoryData<string, object> NativeIntegerArguments() => new()
        {
            { "nint", (nint)1 },
            { "nint zero", (nint)0 },
            { "negative nint", (nint)(-1) },
            { "nint.MaxValue", nint.MaxValue },
            { "nint.MinValue", nint.MinValue },
            { "nuint", (nuint)1 },
            { "nuint.MaxValue", nuint.MaxValue },
            { "nint in a record", new NativeHandle(1, 2) },
            { "other nint in a record", new NativeHandle(2, 2) },
            { "nint[]", new nint[] { 1, -1 } },
            { "nuint[]", new nuint[] { 1, 2 } },
            { "nint in object[]", new object[] { (nint)1 } },
            { "nint in an object property", new Boxed((nint)2) },
            { "long", 1L },
            { "int", 1 },
            { "ulong", 1UL },
        };

        [Theory]
        [InlineData(typeof(FastHashKeyGenerator))]
        [InlineData(typeof(JsonKeyGenerator))]
        public void KeyGenerators_NativeIntegerArguments_GetDistinctStableKeys(Type generatorType)
        {
            var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);
            var args = NativeIntegerArguments().Select(row => row[1]).ToArray();

            var keys = args.Select(a => generator.GenerateKey("M", new[] { a }, policy)).ToArray();
            var again = args.Select(a => generator.GenerateKey("M", new[] { a }, policy)).ToArray();

            Assert.Equal(keys, again);
            Assert.Equal(keys.Length, keys.Distinct().Count());
        }

        [Theory]
        [MemberData(nameof(NativeIntegerArguments))]
        public void JsonKeyGenerator_NativeIntegerArgument_IsWrittenAsANumber(string because, object arg)
        {
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);

            var key = new JsonKeyGenerator().GenerateKey("M", new[] { arg }, policy);

            key.Should().NotContain("{}", because);
            if (arg is nint value)
            {
                key.Should().StartWith($"M:[{((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)}]:", because);
            }
        }

        [Theory]
        [MemberData(nameof(NestedValuesOfDifferentTypes))]
        public void FastHashKeyGenerator_NestedValuesOfDifferentRuntimeTypes_GetDistinctKeys(string because, object first, object second)
            => AssertDistinctButStable(new FastHashKeyGenerator(), because, first, second);

        [Theory]
        [MemberData(nameof(NestedValuesOfDifferentTypes))]
        public void JsonKeyGenerator_NestedValuesOfDifferentRuntimeTypes_GetDistinctKeys(string because, object first, object second)
            => AssertDistinctButStable(new JsonKeyGenerator(), because, first, second);

        [Theory]
        [InlineData(typeof(FastHashKeyGenerator))]
        [InlineData(typeof(JsonKeyGenerator))]
        public void KeyGenerators_NestedObjectValues_GetDistinctKeysForEveryType(Type generatorType)
        {
            // int, long, string, double, float, decimal and enum values that all write "1" or 1 in JSON
            var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);
            object[] ones = [1, 1L, "1", 1.0, 1.0f, 1m, (byte)1, DayOfWeek.Monday, '1', (nint)1, (nuint)1];

            var args = ones.Select(o => (object)new object[] { o })
                .Concat(ones.Select(o => (object)new List<object> { o }))
                .Concat(ones.Select(o => (object)new Boxed(o)))
                .Concat(ones.Select(o => (object)new object[] { new object[] { o } }))
                .Concat(new object[] { new Dictionary<string, object> { ["\uFFFFt"] = "System.Int32", ["v"] = 1 } })
                .ToArray();
            var keys = args.Select(a => generator.GenerateKey("M", new[] { a }, policy)).ToArray();

            Assert.Equal(keys.Length, keys.Distinct().Count());
        }

        [Theory]
        [InlineData(typeof(FastHashKeyGenerator))]
        [InlineData(typeof(JsonKeyGenerator))]
        public void KeyGenerators_NestedValuesOfTheDeclaredType_KeepTheirPlainJson(Type generatorType)
        {
            // Only values whose runtime type differs from the declared one are tagged, so exact values read as before
            var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);

            var key = generator.GenerateKey("M", new object[] { new CircleHolder(new Circle("a", 1)) }, policy);
            var again = generator.GenerateKey("M", new object[] { new CircleHolder(new Circle("a", 1)) }, policy);

            key.Should().Be(again);
            if (generator is JsonKeyGenerator)
            {
                key.Should().StartWith("M:[{\"Circle\":{\"Radius\":1,\"Name\":\"a\"}}]:");
            }
        }

        [Theory]
        [InlineData(typeof(FastHashKeyGenerator))]
        [InlineData(typeof(JsonKeyGenerator))]
        public void KeyGenerators_ObjectArrayHoldingItself_IsRefused(Type generatorType)
        {
            // The runtime-type wrapper must not turn a cycle into unbounded recursion
            var generator = (ICacheKeyGenerator)Activator.CreateInstance(generatorType)!;
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);
            var cycle = new object[1];
            cycle[0] = cycle;

            var act = () => generator.GenerateKey("M", new object[] { cycle }, policy);

            act.Should().Throw<InvalidOperationException>().WithMessage("*can't be serialized*");
        }

        private static void AssertDistinctButStable(ICacheKeyGenerator generator, string because, object first, object second)
        {
            var policy = CacheRuntimePolicy.FromPolicy("M", CachePolicy.Empty, CachePolicyFields.None);

            var firstKey = generator.GenerateKey("M", new[] { first }, policy);
            var secondKey = generator.GenerateKey("M", new[] { second }, policy);

            firstKey.Should().NotBe(secondKey, because);
            generator.GenerateKey("M", new[] { first }, policy).Should().Be(firstKey, "the same arguments give the same key");
        }

        [Fact]
        public void FastHashKeyGenerator_TypeNamesLeaveOutAssemblyVersions()
        {
            // A runtime update changes the versions in Type.FullName of a generic type; keys must not change with it
            FastHashKeyGenerator.TypeName(typeof(List<int>)).Should().Be("System.Collections.Generic.List`1[System.Int32]");
            FastHashKeyGenerator.TypeName(typeof(Dictionary<string, int[]>[]))
                .Should().Be("System.Collections.Generic.Dictionary`2[System.String,System.Int32[]][]");
        }
    }
}
