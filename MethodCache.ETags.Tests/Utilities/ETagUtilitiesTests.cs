using FluentAssertions;
using MethodCache.ETags.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace MethodCache.ETags.Tests.Utilities
{
    public class ETagUtilitiesTests
    {
        [Fact]
        public void GenerateETag_FromBytes_ShouldReturnValidStrongETag()
        {
            // Arrange
            var content = System.Text.Encoding.UTF8.GetBytes("test content");

            // Act
            var etag = ETagUtilities.GenerateETag(content, useWeakETag: false);

            // Assert
            etag.Should().StartWith("\"");
            etag.Should().EndWith("\"");
            etag.Should().NotStartWith("W/");
            etag.Length.Should().BeGreaterThan(2);
        }

        [Fact]
        public void GenerateETag_FromBytes_ShouldReturnValidWeakETag()
        {
            // Arrange
            var content = System.Text.Encoding.UTF8.GetBytes("test content");

            // Act
            var etag = ETagUtilities.GenerateETag(content, useWeakETag: true);

            // Assert
            etag.Should().StartWith("W/\"");
            etag.Should().EndWith("\"");
            etag.Length.Should().BeGreaterThan(4);
        }

        [Fact]
        public void GenerateETag_FromObject_ShouldReturnConsistentETag()
        {
            // Arrange
            var obj1 = new { Name = "Test", Id = 1 };
            var obj2 = new { Name = "Test", Id = 1 };

            // Act
            var etag1 = ETagUtilities.GenerateETag(obj1);
            var etag2 = ETagUtilities.GenerateETag(obj2);

            // Assert
            etag1.Should().Be(etag2);
            etag1.Should().StartWith("\"");
            etag1.Should().EndWith("\"");
        }

        [Fact]
        public void GenerateETag_FromString_ShouldReturnValidETag()
        {
            // Arrange
            var content = "test string content";

            // Act
            var etag = ETagUtilities.GenerateETag(content);

            // Assert
            etag.Should().StartWith("\"");
            etag.Should().EndWith("\"");
            etag.Should().NotStartWith("W/");
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
        public void GenerateETagFromVersion_ShouldReturnValidETag()
        {
            // Arrange
            var version = 123456L;

            // Act
            var etag = ETagUtilities.GenerateETagFromVersion(version);

            // Assert
            etag.Should().StartWith("\"");
            etag.Should().EndWith("\"");
            etag.Should().NotStartWith("W/");
        }

        [Fact]
        public void GenerateCompositeETag_ShouldCombineValues()
        {
            // Arrange
            var values = new object[] { "test", 123, true, null };

            // Act
            var etag = ETagUtilities.GenerateCompositeETag(values);

            // Assert
            etag.Should().StartWith("\"");
            etag.Should().EndWith("\"");
            etag.Should().NotStartWith("W/");
        }

        [Theory]
        [InlineData("\"valid-etag\"", true)]
        [InlineData("W/\"weak-etag\"", true)]
        [InlineData("invalid-etag", false)]
        [InlineData("\"", false)]
        [InlineData("W/\"", false)]
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
        [InlineData("\"test-value\"", "test-value")]
        [InlineData("W/\"weak-value\"", "weak-value")]
        [InlineData("invalid", "invalid")]
        [InlineData("", "")]
        [InlineData(null, null)]
        public void ExtractETagValue_ShouldExtractCorrectly(string? etag, string? expected)
        {
            // Act
            var result = ETagUtilities.ExtractETagValue(etag);

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

        [Theory]
        [InlineData("\"value1\"", "\"value1\"", false, true)]
        [InlineData("\"value1\"", "\"value2\"", false, false)]
        [InlineData("W/\"value1\"", "\"value1\"", false, true)]
        [InlineData("W/\"value1\"", "W/\"value1\"", false, true)]
        [InlineData("W/\"value1\"", "\"value1\"", true, false)]
        [InlineData("\"value1\"", "W/\"value1\"", true, false)]
        [InlineData("\"value1\"", "\"value1\"", true, true)]
        public void ETagsMatch_ShouldCompareCorrectly(string etag1, string etag2, bool strongComparison, bool expected)
        {
            // Act
            var result = ETagUtilities.ETagsMatch(etag1, etag2, strongComparison);

            // Assert
            result.Should().Be(expected);
        }

        [Theory]
        [InlineData("*", new[] { "*" })]
        [InlineData("\"etag1\"", new[] { "\"etag1\"" })]
        [InlineData("\"etag1\", \"etag2\"", new[] { "\"etag1\"", "\"etag2\"" })]
        [InlineData("\"etag1\", W/\"etag2\", \"etag3\"", new[] { "\"etag1\"", "W/\"etag2\"", "\"etag3\"" })]
        [InlineData("", new string[0])]
        [InlineData(null, new string[0])]
        public void ParseIfNoneMatch_ShouldParseCorrectly(string? ifNoneMatch, string[] expected)
        {
            // Act
            var result = ETagUtilities.ParseIfNoneMatch(ifNoneMatch).ToArray();

            // Assert
            result.Should().BeEquivalentTo(expected);
        }

        [Fact]
        public void ParseIfNoneMatch_ShouldIgnoreInvalidETags()
        {
            // Arrange
            var ifNoneMatch = "\"valid1\", invalid, \"valid2\", malformed\"";

            // Act
            var result = ETagUtilities.ParseIfNoneMatch(ifNoneMatch).ToArray();

            // Assert
            result.Should().BeEquivalentTo(new[] { "\"valid1\"", "\"valid2\"" });
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
    }
}