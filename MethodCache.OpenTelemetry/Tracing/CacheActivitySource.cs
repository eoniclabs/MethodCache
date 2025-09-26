using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MethodCache.OpenTelemetry.Configuration;

namespace MethodCache.OpenTelemetry.Tracing;

public interface ICacheActivitySource
{
    Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal);
    Activity? StartCacheOperation(string methodName, string operation = TracingConstants.Operations.Get);
    void SetCacheHit(Activity? activity, bool hit);
    void SetCacheKey(Activity? activity, string key, bool recordFullKey = false);
    void SetCacheTags(Activity? activity, string[]? tags);
    void SetCacheError(Activity? activity, Exception exception);
    void SetHttpCorrelation(Activity? activity);
    void RecordException(Activity? activity, Exception exception);
}

public class CacheActivitySource : ICacheActivitySource
{
    private readonly OpenTelemetryOptions _options;
    private readonly ActivitySource _activitySource;

    public CacheActivitySource(IOptions<OpenTelemetryOptions> options)
    {
        _options = options.Value;
        _activitySource = TracingConstants.ActivitySource;
    }

    public Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        if (!_options.EnableTracing)
            return null;

        return _activitySource.StartActivity(operationName, kind);
    }

    public Activity? StartCacheOperation(string methodName, string operation = TracingConstants.Operations.Get)
    {
        if (!_options.EnableTracing)
            return null;

        var activity = _activitySource.StartActivity($"MethodCache.{operation}", ActivityKind.Internal);

        if (activity != null)
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheMethod, methodName);
            activity.SetTag("component", "methodcache");
            activity.SetTag("cache.operation", operation);

            SetHttpCorrelation(activity);

            // Activity baggage will be automatically propagated by OpenTelemetry
        }

        return activity;
    }

    public void SetCacheHit(Activity? activity, bool hit)
    {
        activity?.SetTag(TracingConstants.AttributeNames.CacheHit, hit);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public void SetCacheKey(Activity? activity, string key, bool recordFullKey = false)
    {
        if (activity == null) return;

        if (_options.RecordCacheKeys && (recordFullKey || !_options.HashCacheKeys))
        {
            activity.SetTag(TracingConstants.AttributeNames.CacheKey, key);
        }
        else if (_options.RecordCacheKeys)
        {
            var hash = ComputeHash(key);
            activity.SetTag(TracingConstants.AttributeNames.CacheKeyHash, hash);
        }
    }

    public void SetCacheTags(Activity? activity, string[]? tags)
    {
        if (activity == null || tags == null || tags.Length == 0) return;

        activity.SetTag(TracingConstants.AttributeNames.CacheTags, string.Join(",", tags));
    }

    public void SetCacheError(Activity? activity, Exception exception)
    {
        if (activity == null) return;

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag(TracingConstants.AttributeNames.CacheError, true);
        activity.SetTag(TracingConstants.AttributeNames.CacheErrorType, exception.GetType().Name);

        RecordException(activity, exception);
    }

    public void SetHttpCorrelation(Activity? activity)
    {
        if (activity == null || !_options.EnableHttpCorrelation) return;

        var currentActivity = Activity.Current;
        if (currentActivity != null)
        {
            foreach (var tag in currentActivity.Tags)
            {
                if (tag.Key.StartsWith("http.") || tag.Key.StartsWith("url."))
                {
                    activity.SetTag(tag.Key, tag.Value);
                }
            }

            if (currentActivity.TraceStateString != null)
            {
                activity.TraceStateString = currentActivity.TraceStateString;
            }
        }
    }

    public void RecordException(Activity? activity, Exception exception)
    {
        if (activity == null) return;

        var tags = new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = exception.Message,
        };

        if (exception.StackTrace != null)
        {
            tags["exception.stacktrace"] = exception.StackTrace;
        }

        activity.AddEvent(new ActivityEvent("exception", DateTimeOffset.UtcNow, tags));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes)[..8];
    }
}