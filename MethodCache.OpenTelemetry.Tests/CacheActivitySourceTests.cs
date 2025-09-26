using System;
using System.Diagnostics;
using FluentAssertions;
using MethodCache.OpenTelemetry.Configuration;
using MethodCache.OpenTelemetry.Tracing;
using Microsoft.Extensions.Options;
using Xunit;

namespace MethodCache.OpenTelemetry.Tests;

public class CacheActivitySourceTests
{
    private readonly CacheActivitySource _activitySource;
    private readonly OpenTelemetryOptions _options;

    public CacheActivitySourceTests()
    {
        _options = new OpenTelemetryOptions
        {
            EnableTracing = true,
            RecordCacheKeys = true,
            HashCacheKeys = true,
            EnableHttpCorrelation = true
        };

        _activitySource = new CacheActivitySource(Options.Create(_options));
    }

    [Fact]
    public void StartActivity_TracingEnabled_ReturnsActivity()
    {
        // Act
        using var activity = _activitySource.StartActivity("test.operation");

        // Assert
        activity.Should().NotBeNull();
        activity!.OperationName.Should().Be("test.operation");
    }

    [Fact]
    public void StartActivity_TracingDisabled_ReturnsNull()
    {
        // Arrange
        _options.EnableTracing = false;
        var disabledSource = new CacheActivitySource(Options.Create(_options));

        // Act
        using var activity = disabledSource.StartActivity("test.operation");

        // Assert
        activity.Should().BeNull();
    }

    [Fact]
    public void StartCacheOperation_SetsCorrectTags()
    {
        // Arrange
        var methodName = "IUserService.GetUser";

        // Act
        using var activity = _activitySource.StartCacheOperation(methodName, TracingConstants.Operations.Get);

        // Assert
        activity.Should().NotBeNull();
        activity!.GetTagItem(TracingConstants.AttributeNames.CacheMethod).Should().Be(methodName);
        activity.GetTagItem("component").Should().Be("methodcache");
        activity.GetTagItem("cache.operation").Should().Be(TracingConstants.Operations.Get);
    }

    [Fact]
    public void SetCacheHit_SetsHitTag()
    {
        // Arrange
        using var activity = new Activity("test").Start();

        // Act
        _activitySource.SetCacheHit(activity, true);

        // Assert
        activity.GetTagItem(TracingConstants.AttributeNames.CacheHit).Should().Be(true);
        activity.Status.Should().Be(ActivityStatusCode.Ok);
    }

    [Fact]
    public void SetCacheKey_WithHashing_SetsHashedKey()
    {
        // Arrange
        using var activity = new Activity("test").Start();
        var key = "user:123:profile";

        // Act
        _activitySource.SetCacheKey(activity, key);

        // Assert
        var hashTag = activity.GetTagItem(TracingConstants.AttributeNames.CacheKeyHash);
        hashTag.Should().NotBeNull();
        hashTag.Should().NotBe(key);
        activity.GetTagItem(TracingConstants.AttributeNames.CacheKey).Should().BeNull();
    }

    [Fact]
    public void SetCacheKey_WithoutHashing_SetsFullKey()
    {
        // Arrange
        _options.HashCacheKeys = false;
        var sourceWithoutHashing = new CacheActivitySource(Options.Create(_options));
        using var activity = new Activity("test").Start();
        var key = "user:123:profile";

        // Act
        sourceWithoutHashing.SetCacheKey(activity, key);

        // Assert
        activity.GetTagItem(TracingConstants.AttributeNames.CacheKey).Should().Be(key);
        activity.GetTagItem(TracingConstants.AttributeNames.CacheKeyHash).Should().BeNull();
    }

    [Fact]
    public void SetCacheKey_RecordKeysDisabled_DoesNotSetKey()
    {
        // Arrange
        _options.RecordCacheKeys = false;
        var sourceWithoutRecording = new CacheActivitySource(Options.Create(_options));
        using var activity = new Activity("test").Start();

        // Act
        sourceWithoutRecording.SetCacheKey(activity, "test_key");

        // Assert
        activity.GetTagItem(TracingConstants.AttributeNames.CacheKey).Should().BeNull();
        activity.GetTagItem(TracingConstants.AttributeNames.CacheKeyHash).Should().BeNull();
    }

    [Fact]
    public void SetCacheTags_SetsTags()
    {
        // Arrange
        using var activity = new Activity("test").Start();
        var tags = new[] { "user", "profile", "v2" };

        // Act
        _activitySource.SetCacheTags(activity, tags);

        // Assert
        activity.GetTagItem(TracingConstants.AttributeNames.CacheTags).Should().Be("user,profile,v2");
    }

    [Fact]
    public void SetCacheError_SetsErrorStatus()
    {
        // Arrange
        using var activity = new Activity("test").Start();
        var exception = new InvalidOperationException("Test error");

        // Act
        _activitySource.SetCacheError(activity, exception);

        // Assert
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("Test error");
        activity.GetTagItem(TracingConstants.AttributeNames.CacheError).Should().Be(true);
        activity.GetTagItem(TracingConstants.AttributeNames.CacheErrorType).Should().Be("InvalidOperationException");
    }

    [Fact]
    public void RecordException_AddsExceptionEvent()
    {
        // Arrange
        using var activity = new Activity("test").Start();
        var exception = new ArgumentNullException("param", "Parameter cannot be null");

        // Act
        _activitySource.RecordException(activity, exception);

        // Assert
        activity.Events.Should().ContainSingle();
        var exceptionEvent = activity.Events.GetEnumerator();
        exceptionEvent.MoveNext();
        var evt = exceptionEvent.Current;
        evt.Name.Should().Be("exception");
        evt.Tags.Should().Contain(t => t.Key == "exception.type" && t.Value!.ToString()!.Contains("ArgumentNullException"));
        evt.Tags.Should().Contain(t => t.Key == "exception.message" && t.Value!.ToString() == "Parameter cannot be null (Parameter 'param')");
    }

    [Fact]
    public void SetHttpCorrelation_WithCurrentActivity_CopiesHttpTags()
    {
        // Arrange
        using var parentActivity = new Activity("parent").Start();
        parentActivity.SetTag("http.method", "GET");
        parentActivity.SetTag("http.path", "/api/users");
        parentActivity.SetTag("url.full", "https://example.com/api/users");
        parentActivity.TraceStateString = "key=value";

        using var childActivity = new Activity("child").Start();

        // Act
        _activitySource.SetHttpCorrelation(childActivity);

        // Assert
        childActivity.GetTagItem("http.method").Should().Be("GET");
        childActivity.GetTagItem("http.path").Should().Be("/api/users");
        childActivity.GetTagItem("url.full").Should().Be("https://example.com/api/users");
        childActivity.TraceStateString.Should().Be("key=value");
    }

    [Fact]
    public void SetHttpCorrelation_DisabledInOptions_DoesNotCopyTags()
    {
        // Arrange
        _options.EnableHttpCorrelation = false;
        var sourceWithoutCorrelation = new CacheActivitySource(Options.Create(_options));

        using var parentActivity = new Activity("parent").Start();
        parentActivity.SetTag("http.method", "GET");

        using var childActivity = new Activity("child").Start();

        // Act
        sourceWithoutCorrelation.SetHttpCorrelation(childActivity);

        // Assert
        childActivity.GetTagItem("http.method").Should().BeNull();
    }
}