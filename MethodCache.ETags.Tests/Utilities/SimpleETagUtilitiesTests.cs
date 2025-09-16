using FluentAssertions;
using MethodCache.ETags.Utilities;
using Xunit;

namespace MethodCache.ETags.Tests.Utilities
{
    public class SimpleETagUtilitiesTests
    {
        [Fact]
        public void GenerateETag_FromString_ShouldReturnValidETag()
        {
            // Arrange
            var content = "test content";

            // Act
            var etag = ETagUtilities.GenerateETag(content);

            // Assert
            etag.Should().StartWith("\"");
            etag.Should().EndWith("\"");
            etag.Should().NotStartWith("W/");
            etag.Length.Should().BeGreaterThan(2);
        }

        [Fact]
        public void GenerateETag_SameContent_ShouldReturnSameETag()
        {
            // Arrange
            var content1 = "identical content";
            var content2 = "identical content";

            // Act
            var etag1 = ETagUtilities.GenerateETag(content1);
            var etag2 = ETagUtilities.GenerateETag(content2);

            // Assert
            etag1.Should().Be(etag2);
        }

        [Fact]
        public void GenerateETag_DifferentContent_ShouldReturnDifferentETags()
        {
            // Arrange
            var content1 = "content one";
            var content2 = "content two";

            // Act
            var etag1 = ETagUtilities.GenerateETag(content1);
            var etag2 = ETagUtilities.GenerateETag(content2);

            // Assert
            etag1.Should().NotBe(etag2);
        }

        [Theory]
        [InlineData("\"valid-etag\"", true)]
        [InlineData("W/\"weak-etag\"", true)]
        [InlineData("invalid-etag", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsValidETag_ShouldValidateCorrectly(string? etag, bool expected)
        {
            // Act
            var result = ETagUtilities.IsValidETag(etag);

            // Assert
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData("\"strong-etag\"", false)]
        [InlineData("W/\"weak-etag\"", true)]
        [InlineData("invalid", false)]
        [InlineData(null, false)]
        public void IsWeakETag_ShouldDetectCorrectly(string? etag, bool expected)
        {
            // Act
            var result = ETagUtilities.IsWeakETag(etag);

            // Assert
            result.Should().Be(expected);
        }

        [Fact]
        public void GenerateETagFromTimestamp_ShouldReturnValidETag()
        {
            // Arrange
            var timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            // Act
            var etag = ETagUtilities.GenerateETagFromTimestamp(timestamp);

            // Assert
            etag.Should().StartWith("\"");
            etag.Should().EndWith("\"");
            etag.Should().NotStartWith("W/");
        }

        [Fact]
        public void ETagsMatch_ShouldCompareCorrectly()
        {
            // Arrange
            var etag1 = "\"value1\"";
            var etag2 = "\"value1\"";
            var etag3 = "\"value2\"";

            // Act & Assert
            ETagUtilities.ETagsMatch(etag1, etag2).Should().BeTrue();
            ETagUtilities.ETagsMatch(etag1, etag3).Should().BeFalse();
        }
    }
}