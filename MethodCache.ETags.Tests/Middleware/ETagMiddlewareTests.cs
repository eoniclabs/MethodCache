using FluentAssertions;
using MethodCache.ETags.Abstractions;
using MethodCache.ETags.Middleware;
using MethodCache.ETags.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.ETags.Tests.Middleware
{
    public class ETagMiddlewareTests
    {
        private readonly Mock<IETagCacheManager> _mockCacheManager;
        private readonly Mock<ILogger<ETagMiddleware>> _mockLogger;
        private readonly ETagMiddlewareOptions _options;
        private readonly ETagMiddleware _middleware;
        private readonly Mock<RequestDelegate> _mockNext;

        public ETagMiddlewareTests()
        {
            _mockCacheManager = new Mock<IETagCacheManager>();
            _mockLogger = new Mock<ILogger<ETagMiddleware>>();
            _options = new ETagMiddlewareOptions
            {
                DefaultExpiration = TimeSpan.FromHours(1),
                CacheableContentTypes = new[] { "application/json", "text/html" },
                SkipPaths = new[] { "/health", "/metrics" }
            };

            var mockOptions = new Mock<IOptions<ETagMiddlewareOptions>>();
            mockOptions.Setup(x => x.Value).Returns(_options);

            _mockNext = new Mock<RequestDelegate>();
            _middleware = new ETagMiddleware(_mockNext.Object, _mockCacheManager.Object, mockOptions.Object, _mockLogger.Object);
        }

        [Fact]
        public async Task InvokeAsync_SkipPath_ShouldCallNextDirectly()
        {
            // Arrange
            var context = CreateHttpContext("/health");

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            _mockNext.Verify(x => x(context), Times.Once);
            _mockCacheManager.Verify(x => x.GetOrCreateWithETagAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                It.IsAny<Core.Runtime.CacheRuntimeDescriptor>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task InvokeAsync_NonCacheableContentType_ShouldCallNextDirectly()
        {
            // Arrange
            var context = CreateHttpContext("/api/test");
            context.Response.ContentType = "application/xml"; // Not in cacheable types

            _mockNext.Setup(x => x(context)).Returns(Task.CompletedTask);

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            _mockNext.Verify(x => x(context), Times.Once);
            _mockCacheManager.Verify(x => x.GetOrCreateWithETagAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                It.IsAny<Core.Runtime.CacheRuntimeDescriptor>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task InvokeAsync_CacheControlNoCache_ShouldCallNextDirectly()
        {
            // Arrange
            var context = CreateHttpContext("/api/test");
            context.Request.Headers["Cache-Control"] = "no-cache";

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            _mockNext.Verify(x => x(context), Times.Once);
            _mockCacheManager.Verify(x => x.GetOrCreateWithETagAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                It.IsAny<Core.Runtime.CacheRuntimeDescriptor>(),
                It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task InvokeAsync_ETagMatch_ShouldReturn304()
        {
            // Arrange
            var context = CreateHttpContext("/api/test");
            context.Request.Headers["If-None-Match"] = "\"matching-etag\"";
            context.Response.ContentType = "application/json";

            var cacheResult = ETagCacheResult<ResponseCacheEntry>.NotModified("\"matching-etag\"", DateTime.UtcNow);

            _mockCacheManager
                .Setup(x => x.GetOrCreateWithETagAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                    It.IsAny<Core.Runtime.CacheRuntimeDescriptor>(),
                    "\"matching-etag\""))
                .ReturnsAsync(cacheResult);

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(304);
            context.Response.Headers["ETag"].ToString().Should().Be("\"matching-etag\"");
            _mockNext.Verify(x => x(context), Times.Never);
        }

        [Fact]
        public async Task InvokeAsync_CacheHit_ShouldReturnCachedResponse()
        {
            // Arrange
            var context = CreateHttpContext("/api/test");
            var responseBody = "cached response content";
            var responseBytes = Encoding.UTF8.GetBytes(responseBody);
            
            var cacheEntry = new ResponseCacheEntry
            {
                Body = responseBytes,
                ContentType = "application/json",
                StatusCode = 200,
                Headers = new Dictionary<string, string> { { "X-Custom", "value" } }
            };

            var cacheResult = ETagCacheResult<ResponseCacheEntry>.Hit(cacheEntry, "\"cache-etag\"", DateTime.UtcNow);

            _mockCacheManager
                .Setup(x => x.GetOrCreateWithETagAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Core.Runtime.CacheRuntimeDescriptor?>()))
                .ReturnsAsync(cacheResult);

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            context.Response.StatusCode.Should().Be(200);
            context.Response.Headers["ETag"].ToString().Should().Be("\"cache-etag\"");
            context.Response.ContentType.Should().Be("application/json");
            context.Response.Headers["X-Custom"].ToString().Should().Be("value");
            _mockNext.Verify(x => x(context), Times.Never);

            // Read response body
            context.Response.Body.Position = 0;
            var reader = new StreamReader(context.Response.Body);
            var actualBody = await reader.ReadToEndAsync();
            actualBody.Should().Be(responseBody);
        }

        [Fact]
        public async Task InvokeAsync_CacheMiss_ShouldExecuteNextAndCache()
        {
            // Arrange
            var context = CreateHttpContext("/api/test");
            var responseContent = "generated response";
            
            _mockNext.Setup(x => x(context)).Callback<HttpContext>(ctx =>
            {
                ctx.Response.ContentType = "application/json";
                ctx.Response.StatusCode = 200;
                ctx.Response.Headers["X-Generated"] = "true";
                var bytes = Encoding.UTF8.GetBytes(responseContent);
                ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
            });

            var generatedETag = "\"generated-etag\"";
            ETagCacheEntry<ResponseCacheEntry>? capturedEntry = null;

            _mockCacheManager
                .Setup(x => x.GetOrCreateWithETagAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Core.Runtime.CacheRuntimeDescriptor?>()))
                .Returns<string, Func<Task<ETagCacheEntry<ResponseCacheEntry>>>, Core.Runtime.CacheRuntimeDescriptor, string?>(
                    async (key, factory, settings, ifNoneMatch) =>
                    {
                        capturedEntry = await factory();
                        return ETagCacheResult<ResponseCacheEntry>.Miss(capturedEntry.Value!, generatedETag, DateTime.UtcNow);
                    });

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            context.Response.Headers["ETag"].ToString().Should().Be(generatedETag);
            capturedEntry.Should().NotBeNull();
            capturedEntry!.Value.Should().NotBeNull();
            capturedEntry.Value!.ContentType.Should().Be("application/json");
            capturedEntry.Value.StatusCode.Should().Be(200);
            capturedEntry.Value.Headers.Should().ContainKey("X-Generated");

            var bodyContent = Encoding.UTF8.GetString(capturedEntry.Value.Body);
            bodyContent.Should().Be(responseContent);
        }

        [Fact]
        public async Task InvokeAsync_WithCacheControlHeader_ShouldAddCacheControlToResponse()
        {
            // Arrange
            _options.AddCacheControlHeader = true;
            _options.DefaultCacheMaxAge = TimeSpan.FromMinutes(30);
            
            var context = CreateHttpContext("/api/test");
            var cacheResult = ETagCacheResult<ResponseCacheEntry>.Hit(
                new ResponseCacheEntry
                {
                    Body = Encoding.UTF8.GetBytes("test"),
                    ContentType = "application/json",
                    StatusCode = 200,
                    Headers = new Dictionary<string, string>()
                },
                "\"test-etag\"",
                DateTime.UtcNow);

            _mockCacheManager
                .Setup(x => x.GetOrCreateWithETagAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Core.Runtime.CacheRuntimeDescriptor?>()))
                .ReturnsAsync(cacheResult);

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            context.Response.Headers["Cache-Control"].ToString().Should().Be("public, max-age=1800");
        }

        [Fact]
        public async Task InvokeAsync_WithLastModifiedHeader_ShouldAddLastModifiedToResponse()
        {
            // Arrange
            _options.AddLastModifiedHeader = true;
            var lastModified = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            
            var context = CreateHttpContext("/api/test");
            var cacheResult = ETagCacheResult<ResponseCacheEntry>.Hit(
                new ResponseCacheEntry
                {
                    Body = Encoding.UTF8.GetBytes("test"),
                    ContentType = "application/json",
                    StatusCode = 200,
                    Headers = new Dictionary<string, string>()
                },
                "\"test-etag\"",
                lastModified);

            _mockCacheManager
                .Setup(x => x.GetOrCreateWithETagAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Core.Runtime.CacheRuntimeDescriptor?>()))
                .ReturnsAsync(cacheResult);

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            context.Response.Headers["Last-Modified"].ToString().Should().Be(lastModified.ToString("R"));
        }

        [Theory]
        [InlineData("GET")]
        [InlineData("HEAD")]
        public async Task InvokeAsync_CacheableHttpMethods_ShouldProcessETag(string httpMethod)
        {
            // Arrange
            var context = CreateHttpContext("/api/test");
            context.Request.Method = httpMethod;
            
            var cacheResult = ETagCacheResult<ResponseCacheEntry>.Hit(
                new ResponseCacheEntry
                {
                    Body = Encoding.UTF8.GetBytes("test"),
                    ContentType = "application/json",
                    StatusCode = 200,
                    Headers = new Dictionary<string, string>()
                },
                "\"test-etag\"",
                DateTime.UtcNow);

            _mockCacheManager
                .Setup(x => x.GetOrCreateWithETagAsync(
                    It.IsAny<string>(),
                    It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                    It.IsAny<string?>(),
                    It.IsAny<Core.Runtime.CacheRuntimeDescriptor?>()))
                .ReturnsAsync(cacheResult);

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            context.Response.Headers["ETag"].ToString().Should().Be("\"test-etag\"");
            _mockCacheManager.Verify(x => x.GetOrCreateWithETagAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                It.IsAny<Core.Runtime.CacheRuntimeDescriptor>(),
                It.IsAny<string?>()), Times.Once);
        }

        [Theory]
        [InlineData("POST")]
        [InlineData("PUT")]
        [InlineData("DELETE")]
        [InlineData("PATCH")]
        public async Task InvokeAsync_NonCacheableHttpMethods_ShouldCallNextDirectly(string httpMethod)
        {
            // Arrange
            var context = CreateHttpContext("/api/test");
            context.Request.Method = httpMethod;

            // Act
            await _middleware.InvokeAsync(context);

            // Assert
            _mockNext.Verify(x => x(context), Times.Once);
            _mockCacheManager.Verify(x => x.GetOrCreateWithETagAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<ETagCacheEntry<ResponseCacheEntry>>>>(),
                It.IsAny<Core.Runtime.CacheRuntimeDescriptor>(),
                It.IsAny<string?>()), Times.Never);
        }

        private HttpContext CreateHttpContext(string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            context.Request.Method = "GET";
            context.Response.Body = new MemoryStream();
            return context;
        }
    }
}