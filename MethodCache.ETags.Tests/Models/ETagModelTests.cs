using FluentAssertions;
using MethodCache.ETags.Models;
using System;
using Xunit;

namespace MethodCache.ETags.Tests.Models
{
    public class ETagModelTests
    {
        [Fact]
        public void ETagCacheEntry_WithValue_ShouldCreateCorrectly()
        {
            // Arrange
            var value = "test-value";
            var etag = "\"test-etag\"";

            // Act
            var entry = ETagCacheEntry<string>.WithValue(value, etag);

            // Assert
            entry.Should().NotBeNull();
            entry.Value.Should().Be(value);
            entry.ETag.Should().Be(etag);
            entry.LastModified.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void ETagCacheEntry_NotModified_ShouldCreateCorrectly()
        {
            // Arrange
            var etag = "\"not-modified-etag\"";

            // Act
            var entry = ETagCacheEntry<string>.NotModified(etag);

            // Assert
            entry.Should().NotBeNull();
            entry.Value.Should().BeNull();
            entry.ETag.Should().Be(etag);
            entry.LastModified.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void ETagCacheEntry_WithValueAndTimestamp_ShouldCreateCorrectly()
        {
            // Arrange
            var value = 42;
            var etag = "\"numeric-etag\"";
            var timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

            // Act
            var entry = ETagCacheEntry<int>.WithValue(value, etag, timestamp);

            // Assert
            entry.Should().NotBeNull();
            entry.Value.Should().Be(value);
            entry.ETag.Should().Be(etag);
            entry.LastModified.Should().Be(timestamp);
        }

        [Fact]
        public void ETagCacheResult_Hit_ShouldHaveCorrectProperties()
        {
            // Arrange
            var value = "cached-value";
            var etag = "\"hit-etag\"";
            var lastModified = DateTime.UtcNow;

            // Act
            var result = new ETagCacheResult<string>
            {
                Value = value,
                ETag = etag,
                Status = ETagCacheStatus.Hit,
                ShouldReturn304 = false,
                LastModified = lastModified
            };

            // Assert
            result.Value.Should().Be(value);
            result.ETag.Should().Be(etag);
            result.Status.Should().Be(ETagCacheStatus.Hit);
            result.ShouldReturn304.Should().BeFalse();
            result.LastModified.Should().Be(lastModified);
        }

        [Fact]
        public void ETagCacheResult_NotModified_ShouldHaveCorrectProperties()
        {
            // Arrange
            var etag = "\"not-modified-etag\"";
            var lastModified = DateTime.UtcNow;

            // Act
            var result = new ETagCacheResult<string>
            {
                Value = null,
                ETag = etag,
                Status = ETagCacheStatus.NotModified,
                ShouldReturn304 = true,
                LastModified = lastModified
            };

            // Assert
            result.Value.Should().BeNull();
            result.ETag.Should().Be(etag);
            result.Status.Should().Be(ETagCacheStatus.NotModified);
            result.ShouldReturn304.Should().BeTrue();
            result.LastModified.Should().Be(lastModified);
        }

        [Fact]
        public void ETagCacheResult_Miss_ShouldHaveCorrectProperties()
        {
            // Arrange
            var value = "new-value";
            var etag = "\"miss-etag\"";

            // Act
            var result = new ETagCacheResult<string>
            {
                Value = value,
                ETag = etag,
                Status = ETagCacheStatus.Miss,
                ShouldReturn304 = false,
                LastModified = DateTime.UtcNow
            };

            // Assert
            result.Value.Should().Be(value);
            result.ETag.Should().Be(etag);
            result.Status.Should().Be(ETagCacheStatus.Miss);
            result.ShouldReturn304.Should().BeFalse();
        }

        [Fact]
        public void ETagCacheResult_FromEntry_ShouldCreateFromEntry()
        {
            // Arrange
            var value = "entry-value";
            var etag = "\"entry-etag\"";
            var timestamp = DateTime.UtcNow;
            var entry = ETagCacheEntry<string>.WithValue(value, etag, timestamp);

            // Act
            var result = ETagCacheResult<string>.FromEntry(entry, ETagCacheStatus.Hit);

            // Assert
            result.Value.Should().Be(value);
            result.ETag.Should().Be(etag);
            result.Status.Should().Be(ETagCacheStatus.Hit);
            result.ShouldReturn304.Should().BeFalse();
            result.LastModified.Should().Be(timestamp);
        }

        [Fact]
        public void ResponseCacheEntry_ShouldStoreAllProperties()
        {
            // Arrange
            var body = System.Text.Encoding.UTF8.GetBytes("response content");
            var contentType = "application/json";
            var statusCode = 200;
            var headers = new Dictionary<string, string>
            {
                { "X-Custom-Header", "custom-value" },
                { "X-Another-Header", "another-value" }
            };

            // Act
            var entry = new ResponseCacheEntry
            {
                Body = body,
                ContentType = contentType,
                StatusCode = statusCode,
                Headers = headers
            };

            // Assert
            entry.Body.Should().BeEquivalentTo(body);
            entry.ContentType.Should().Be(contentType);
            entry.StatusCode.Should().Be(statusCode);
            entry.Headers.Should().BeEquivalentTo(headers);
        }

        [Fact]
        public void ResponseCacheEntry_WithEmptyHeaders_ShouldHandleCorrectly()
        {
            // Act
            var entry = new ResponseCacheEntry
            {
                Body = Array.Empty<byte>(),
                ContentType = "text/plain",
                StatusCode = 204,
                Headers = new Dictionary<string, string>()
            };

            // Assert
            entry.Body.Should().BeEmpty();
            entry.ContentType.Should().Be("text/plain");
            entry.StatusCode.Should().Be(204);
            entry.Headers.Should().BeEmpty();
        }

        [Theory]
        [InlineData(ETagCacheStatus.Hit)]
        [InlineData(ETagCacheStatus.Miss)]
        [InlineData(ETagCacheStatus.NotModified)]
        public void ETagCacheStatus_AllValues_ShouldBeValid(ETagCacheStatus status)
        {
            // Act & Assert
            Enum.IsDefined(typeof(ETagCacheStatus), status).Should().BeTrue();
        }

        [Fact]
        public void ETagCacheEntry_WithNullValue_ShouldAllowNull()
        {
            // Arrange
            var etag = "\"null-value-etag\"";

            // Act
            var entry = ETagCacheEntry<string?>.WithValue(null, etag);

            // Assert
            entry.Should().NotBeNull();
            entry.Value.Should().BeNull();
            entry.ETag.Should().Be(etag);
        }

        [Fact]
        public void ETagCacheEntry_WithComplexObject_ShouldStoreCorrectly()
        {
            // Arrange
            var complexObject = new { Name = "Test", Id = 123, Items = new[] { 1, 2, 3 } };
            var etag = "\"complex-object-etag\"";

            // Act
            var entry = ETagCacheEntry<object>.WithValue(complexObject, etag);

            // Assert
            entry.Should().NotBeNull();
            entry.Value.Should().BeEquivalentTo(complexObject);
            entry.ETag.Should().Be(etag);
        }
    }
}