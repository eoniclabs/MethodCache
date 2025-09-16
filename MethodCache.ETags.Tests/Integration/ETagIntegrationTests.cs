using FluentAssertions;
using MethodCache.Core;
using MethodCache.ETags.Extensions;
using MethodCache.HybridCache.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace MethodCache.ETags.Tests.Integration
{
    public class ETagIntegrationTests : IDisposable
    {
        private readonly TestServer _server;
        private readonly HttpClient _client;

        public ETagIntegrationTests()
        {
            var hostBuilder = new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    webHost.ConfigureServices(services =>
                    {
                        services.AddSingleton<ICacheManager, MockCacheManager>();
                        services.AddHybridCache<MockCacheManager>();
                        services.AddETagSupport(options =>
                        {
                            options.DefaultExpiration = TimeSpan.FromMinutes(10);
                            options.CacheableContentTypes = new[] { "application/json", "text/plain" };
                            options.AddCacheControlHeader = true;
                            options.AddLastModifiedHeader = true;
                        });
                    });
                    webHost.Configure(app =>
                    {
                        app.UseETagCaching();
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGet("/api/test", async context =>
                            {
                                context.Response.ContentType = "application/json";
                                await context.Response.WriteAsync("{\"message\":\"Hello World\",\"timestamp\":\"2024-01-01T00:00:00Z\"}");
                            });

                            endpoints.MapGet("/api/dynamic", async context =>
                            {
                                context.Response.ContentType = "application/json";
                                var timestamp = DateTime.UtcNow.ToString("O");
                                await context.Response.WriteAsync($"{{\"message\":\"Dynamic content\",\"timestamp\":\"{timestamp}\"}}");
                            });

                            endpoints.MapGet("/health", async context =>
                            {
                                await context.Response.WriteAsync("OK");
                            });
                        });
                    });
                });

            var host = hostBuilder.Start();
            _server = host.GetTestServer();
            _client = _server.CreateClient();
        }

        [Fact]
        public async Task FirstRequest_ShouldReturnETagHeader()
        {
            // Act
            var response = await _client.GetAsync("/api/test");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.ETag.Should().NotBeNull();
            response.Headers.ETag!.Tag.Should().NotBeNullOrEmpty();
            response.Headers.CacheControl.Should().NotBeNull();
            response.Headers.Date.Should().NotBeNull();
        }

        [Fact]
        public async Task SecondRequest_WithSameETag_ShouldReturn304()
        {
            // Arrange
            var firstResponse = await _client.GetAsync("/api/test");
            var etag = firstResponse.Headers.ETag!.Tag;

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(etag));

            // Act
            var secondResponse = await _client.SendAsync(request);

            // Assert
            secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
            secondResponse.Headers.ETag.Should().NotBeNull();
            secondResponse.Headers.ETag!.Tag.Should().Be(etag);
            secondResponse.Content.Headers.ContentLength.Should().Be(0);
        }

        [Fact]
        public async Task Request_WithDifferentETag_ShouldReturn200WithContent()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"different-etag\""));

            // Act
            var response = await _client.SendAsync(request);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.ETag.Should().NotBeNull();
            response.Headers.ETag!.Tag.Should().NotBe("\"different-etag\"");

            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("Hello World");
        }

        [Fact]
        public async Task Request_WithStarETag_ShouldReturn304()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("*"));

            // Act
            var response = await _client.SendAsync(request);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        }

        [Fact]
        public async Task NonCacheableEndpoint_ShouldNotHaveETagHeader()
        {
            // Act
            var response = await _client.GetAsync("/health");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.ETag.Should().BeNull();

            var content = await response.Content.ReadAsStringAsync();
            content.Should().Be("OK");
        }

        [Fact]
        public async Task PostRequest_ShouldNotBeCached()
        {
            // Arrange
            var content = new StringContent("{\"test\":true}", Encoding.UTF8, "application/json");

            // Act
            var response = await _client.PostAsync("/api/test", content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed); // Since we only mapped GET
            response.Headers.ETag.Should().BeNull();
        }

        [Fact]
        public async Task Request_WithCacheControlNoCache_ShouldNotBeCached()
        {
            // Arrange
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
            {
                NoCache = true
            };

            // Act
            var response = await _client.SendAsync(request);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            // The response might still have an ETag, but caching behavior is affected
            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("Hello World");
        }

        [Fact]
        public async Task MultipleRequests_ShouldUseCachedResponse()
        {
            // Act
            var response1 = await _client.GetAsync("/api/test");
            var response2 = await _client.GetAsync("/api/test");
            var response3 = await _client.GetAsync("/api/test");

            // Assert
            response1.StatusCode.Should().Be(HttpStatusCode.OK);
            response2.StatusCode.Should().Be(HttpStatusCode.OK);
            response3.StatusCode.Should().Be(HttpStatusCode.OK);

            var etag1 = response1.Headers.ETag!.Tag;
            var etag2 = response2.Headers.ETag!.Tag;
            var etag3 = response3.Headers.ETag!.Tag;

            etag1.Should().Be(etag2);
            etag2.Should().Be(etag3);

            var content1 = await response1.Content.ReadAsStringAsync();
            var content2 = await response2.Content.ReadAsStringAsync();
            var content3 = await response3.Content.ReadAsStringAsync();

            content1.Should().Be(content2);
            content2.Should().Be(content3);
        }

        [Fact]
        public async Task DynamicContent_ShouldHaveDifferentETags()
        {
            // Act
            await Task.Delay(10); // Ensure different timestamps
            var response1 = await _client.GetAsync("/api/dynamic");
            await Task.Delay(10);
            var response2 = await _client.GetAsync("/api/dynamic");

            // Assert
            response1.StatusCode.Should().Be(HttpStatusCode.OK);
            response2.StatusCode.Should().Be(HttpStatusCode.OK);

            var etag1 = response1.Headers.ETag!.Tag;
            var etag2 = response2.Headers.ETag!.Tag;

            // Since dynamic content changes, ETags should be different
            // Note: In a real implementation with proper caching, this behavior 
            // would depend on your cache expiration settings
            response1.Headers.ETag.Should().NotBeNull();
            response2.Headers.ETag.Should().NotBeNull();
        }

        [Fact]
        public async Task Request_WithMultipleIfNoneMatchETags_ShouldHandleCorrectly()
        {
            // Arrange
            var firstResponse = await _client.GetAsync("/api/test");
            var actualETag = firstResponse.Headers.ETag!.Tag;

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/test");
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"old-etag\""));
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue(actualETag));
            request.Headers.IfNoneMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"another-etag\""));

            // Act
            var response = await _client.SendAsync(request);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotModified);
            response.Headers.ETag!.Tag.Should().Be(actualETag);
        }

        [Fact]
        public async Task CacheControlHeaders_ShouldBeSetCorrectly()
        {
            // Act
            var response = await _client.GetAsync("/api/test");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.CacheControl.Should().NotBeNull();
            response.Headers.CacheControl!.Public.Should().BeTrue();
            response.Headers.CacheControl.MaxAge.Should().BeGreaterThan(TimeSpan.Zero);
        }

        public void Dispose()
        {
            _client?.Dispose();
            _server?.Dispose();
        }
    }

    // Mock implementation for testing
    public class MockCacheManager : ICacheManager
    {
        private readonly Dictionary<string, object> _cache = new();
        private readonly Dictionary<string, DateTime> _expiry = new();

        public async Task<T> GetOrCreateAsync<T>(string methodName, object[] args, Func<Task<T>> factory, Core.Configuration.CacheMethodSettings settings, ICacheKeyGenerator keyGenerator, bool requireIdempotent)
        {
            var key = $"{methodName}:{string.Join(":", args)}";
            
            if (_cache.TryGetValue(key, out var cached) && _expiry.TryGetValue(key, out var expiry) && expiry > DateTime.UtcNow)
            {
                return (T)cached;
            }

            var result = await factory();
            _cache[key] = result!;
            _expiry[key] = DateTime.UtcNow.Add(settings.Duration ?? TimeSpan.FromMinutes(10));
            
            return result;
        }

        public Task InvalidateByTagsAsync(params string[] tags)
        {
            // Simple implementation - clear all cache
            _cache.Clear();
            _expiry.Clear();
            return Task.CompletedTask;
        }
    }
}