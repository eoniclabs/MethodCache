using System.Globalization;
using MethodCache.Infrastructure;
using Xunit;

namespace MethodCache.Core.Tests.Core
{
    public class MethodCacheKeyBuilderTests
    {
        private enum Color { Red = 1, Blue = 7 }

        private enum SByteColor : sbyte { Min = sbyte.MinValue }

        private enum UShortColor : ushort { Max = ushort.MaxValue }

        public static TheoryData<string, string> SameTextAsMethodCacheKeys()
        {
            var local = new DateTime(2026, 10, 11, 9, 30, 0, DateTimeKind.Local);
            return new TheoryData<string, string>
            {
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(42)), MethodCacheKeys.Part(42) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(-7L)), MethodCacheKeys.Part(-7L) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(ulong.MaxValue)), MethodCacheKeys.Part(ulong.MaxValue) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart((byte)200)), MethodCacheKeys.Part((byte)200) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart((sbyte)-5)), MethodCacheKeys.Part((sbyte)-5) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart((short)-300)), MethodCacheKeys.Part((short)-300) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart((ushort)65000)), MethodCacheKeys.Part((ushort)65000) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(4000000000u)), MethodCacheKeys.Part(4000000000u) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(0.1 + 0.2)), MethodCacheKeys.Part(0.1 + 0.2) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(1.5e-10f)), MethodCacheKeys.Part(1.5e-10f) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(double.NaN)), MethodCacheKeys.Part(double.NaN) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(12345.6789m)), MethodCacheKeys.Part(12345.6789m) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(true)), MethodCacheKeys.Part(true) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(false)), MethodCacheKeys.Part(false) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(':')), MethodCacheKeys.Part(':') },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart('x')), MethodCacheKeys.Part('x') },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart("plain")), MethodCacheKeys.Part("plain") },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(@"a:b\c::")), MethodCacheKeys.Part(@"a:b\c::") },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart("")), MethodCacheKeys.Part("") },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart((string?)null)), MethodCacheKeys.Part((string?)null) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendNullPart()), MethodCacheKeys.Null },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"))), MethodCacheKeys.Part(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(local)), MethodCacheKeys.Part(local) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc))), MethodCacheKeys.Part(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)))), MethodCacheKeys.Part(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2))) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart(Color.Blue)), MethodCacheKeys.Part(Color.Blue) },
                { Build((ref MethodCacheKeyBuilder b) => b.AppendPart((Color)99)), MethodCacheKeys.Part((Color)99) },
            };
        }

        [Theory]
        [MemberData(nameof(SameTextAsMethodCacheKeys))]
        public void AppendPart_WritesTheSameTextAsMethodCacheKeys(string built, string part)
        {
            Assert.Equal("Prefix" + MethodCacheKeys.Separator + part, built);
        }

        [Fact]
        public void AppendPart_UsesTheInvariantCulture()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
            try
            {
                Assert.Equal("Prefix:1.5", Build((ref MethodCacheKeyBuilder b) => b.AppendPart(1.5)));
                Assert.Equal("Prefix:-3", Build((ref MethodCacheKeyBuilder b) => b.AppendPart(-3)));

                // Found by CacheKeyPropertyTests: Enum.TryFormat used the culture's minus sign (U+2212)
                Assert.Equal("Prefix:-1", Build((ref MethodCacheKeyBuilder b) => b.AppendPart((Color)(-1))));
                Assert.Equal("Prefix:-128", Build((ref MethodCacheKeyBuilder b) => b.AppendPart((SByteColor)sbyte.MinValue)));
                Assert.Equal("Prefix:65535", Build((ref MethodCacheKeyBuilder b) => b.AppendPart((UShortColor)ushort.MaxValue)));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Builder_MatchesMethodCacheKeysFor_WithSeveralParts()
        {
            var builder = new MethodCacheKeyBuilder(stackalloc char[MethodCacheKeyBuilder.StackBufferSize]);
            builder.Append(MethodCacheKeys.Prefix(typeof(MethodCacheKeyBuilderTests), "Get"));
            builder.AppendPart(7);
            builder.AppendPart("tenant:1");
            builder.AppendNullPart();

            var expected = MethodCacheKeys.For(typeof(MethodCacheKeyBuilderTests), "Get",
                MethodCacheKeys.Part(7), MethodCacheKeys.Part("tenant:1"), MethodCacheKeys.Null);
            Assert.Equal(expected, builder.ToString());
            Assert.True(builder.AsSpan().SequenceEqual(expected));
        }

        [Fact]
        public void Builder_GrowsPastItsInitialBuffer()
        {
            var longText = new string('a', 300) + ":" + new string('b', 300);
            var builder = new MethodCacheKeyBuilder(stackalloc char[8]);
            builder.Append("Prefix");
            builder.AppendPart(longText);
            builder.AppendPart(Guid.Empty);
            builder.AppendPart(DateTimeOffset.MaxValue);

            Assert.Equal(
                "Prefix:" + MethodCacheKeys.Part(longText) + ":" + MethodCacheKeys.Part(Guid.Empty) + ":" + MethodCacheKeys.Part(DateTimeOffset.MaxValue),
                builder.ToString());
        }

        private delegate void BuildAction(ref MethodCacheKeyBuilder builder);

        private static string Build(BuildAction append)
        {
            var builder = new MethodCacheKeyBuilder(stackalloc char[16]);
            builder.Append("Prefix");
            append(ref builder);
            return builder.ToString();
        }
    }
}
