using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.OpenTelemetry.Propagators;
using MethodCache.OpenTelemetry.Tracing;

namespace MethodCache.OpenTelemetry.Instrumentation;

public interface IHttpCorrelationEnricher
{
    void EnrichWithHttpContext(Activity? activity);
    void EnrichFromHttpContext(Activity? activity, HttpContext? httpContext);
    string? GetRequestId();
    string? GetTraceId();
    string? GetSpanId();
}

public class HttpCorrelationEnricher : IHttpCorrelationEnricher
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpCorrelationEnricher(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public void EnrichWithHttpContext(Activity? activity)
    {
        if (activity == null) return;

        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null) return;

        EnrichFromHttpContext(activity, httpContext);
    }

    public void EnrichFromHttpContext(Activity? activity, HttpContext? httpContext)
    {
        if (activity == null || httpContext == null) return;

        var request = httpContext.Request;
        var response = httpContext.Response;

        activity.SetTag(TracingConstants.AttributeNames.HttpMethod, request.Method);
        activity.SetTag(TracingConstants.AttributeNames.HttpPath, request.Path.Value);
        activity.SetTag("http.scheme", request.Scheme);
        activity.SetTag("http.host", request.Host.Value);
        activity.SetTag("http.query", request.QueryString.Value);
        activity.SetTag("http.user_agent", request.Headers["User-Agent"].ToString());

        if (httpContext.TraceIdentifier != null)
        {
            activity.SetTag(TracingConstants.AttributeNames.HttpRequestId, httpContext.TraceIdentifier);
        }

        if (response.HasStarted)
        {
            activity.SetTag(TracingConstants.AttributeNames.HttpStatusCode, response.StatusCode);
        }

        if (httpContext.User?.Identity?.IsAuthenticated == true)
        {
            activity.SetTag("user.authenticated", true);

            var userId = httpContext.User.FindFirst("sub")?.Value
                        ?? httpContext.User.FindFirst("id")?.Value
                        ?? httpContext.User.Identity.Name;

            if (!string.IsNullOrEmpty(userId))
            {
                activity.SetTag("user.id", HashUserId(userId));
            }
        }

        foreach (var header in request.Headers)
        {
            if (IsTracingHeader(header.Key))
            {
                activity.SetTag($"http.request.header.{header.Key.ToLower()}", header.Value.ToString());
            }
        }

        if (Activity.Current != null && Activity.Current != activity)
        {
            activity.SetParentId(Activity.Current.TraceId, Activity.Current.SpanId);
        }
    }

    public string? GetRequestId()
    {
        return _httpContextAccessor.HttpContext?.TraceIdentifier;
    }

    public string? GetTraceId()
    {
        return Activity.Current?.TraceId.ToString();
    }

    public string? GetSpanId()
    {
        return Activity.Current?.SpanId.ToString();
    }

    private static bool IsTracingHeader(string headerName)
    {
        var lowerHeader = headerName.ToLower();
        return lowerHeader.StartsWith("x-trace-") ||
               lowerHeader.StartsWith("x-correlation-") ||
               lowerHeader.StartsWith("x-request-") ||
               lowerHeader == "traceparent" ||
               lowerHeader == "tracestate" ||
               lowerHeader == "x-b3-traceid" ||
               lowerHeader == "x-b3-spanid";
    }

    private static string HashUserId(string userId)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(userId);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash)[..8];
    }
}

public class HttpCorrelationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpCorrelationEnricher _enricher;
    private readonly IBaggagePropagator _baggagePropagator;

    public HttpCorrelationMiddleware(
        RequestDelegate next,
        IHttpCorrelationEnricher enricher,
        IBaggagePropagator baggagePropagator)
    {
        _next = next;
        _enricher = enricher;
        _baggagePropagator = baggagePropagator;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        using var activity = TracingConstants.ActivitySource.StartActivity(
            "http.request",
            ActivityKind.Server);

        if (activity != null)
        {
            _enricher.EnrichFromHttpContext(activity, context);

            var correlationId = context.Request.Headers["X-Correlation-Id"].ToString();
            if (!string.IsNullOrEmpty(correlationId))
            {
                _baggagePropagator.SetCacheCorrelationId(correlationId);
            }
            else
            {
                correlationId = Guid.NewGuid().ToString();
                _baggagePropagator.SetCacheCorrelationId(correlationId);
                context.Response.Headers["X-Correlation-Id"] = correlationId;
            }

            var userId = context.User?.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                _baggagePropagator.SetCacheUserId(userId);
            }

            var tenantId = context.Request.Headers["X-Tenant-Id"].ToString();
            if (!string.IsNullOrEmpty(tenantId))
            {
                _baggagePropagator.SetCacheTenantId(tenantId);
            }
        }

        await _next(context);

        if (activity != null && context.Response.HasStarted)
        {
            activity.SetTag(TracingConstants.AttributeNames.HttpStatusCode, context.Response.StatusCode);
        }
    }
}