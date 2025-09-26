using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MethodCache.OpenTelemetry.Correlation;

/// <summary>
/// Middleware to automatically manage correlation contexts for HTTP requests
/// </summary>
public class CorrelationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IAdvancedCorrelationManager _correlationManager;
    private readonly ILogger<CorrelationMiddleware> _logger;

    public CorrelationMiddleware(
        RequestDelegate next,
        IAdvancedCorrelationManager correlationManager,
        ILogger<CorrelationMiddleware> logger)
    {
        _next = next;
        _correlationManager = correlationManager;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ExtractCorrelationId(context);
        var operationName = $"{context.Request.Method} {context.Request.Path}";

        var correlationContext = _correlationManager.StartCorrelation(
            CorrelationType.HttpRequest,
            operationName,
            correlationId);

        try
        {
            // Set HTTP-specific properties
            _correlationManager.SetProperty("http.method", context.Request.Method);
            _correlationManager.SetProperty("http.path", context.Request.Path.ToString());
            _correlationManager.SetProperty("http.query", context.Request.QueryString.ToString());
            _correlationManager.SetProperty("http.user_agent", context.Request.Headers["User-Agent"].ToString());

            // Set user context if available
            if (context.User?.Identity?.IsAuthenticated == true)
            {
                var userId = context.User.FindFirst("sub")?.Value ?? context.User.Identity.Name;
                if (!string.IsNullOrEmpty(userId))
                {
                    _correlationManager.SetProperty("user.id", userId);
                }
            }

            // Set tenant context if available
            var tenantId = context.Request.Headers["X-Tenant-Id"].ToString();
            if (!string.IsNullOrEmpty(tenantId))
            {
                _correlationManager.SetProperty("tenant.id", tenantId);
            }

            // Set session context if available
            if (context.Session?.IsAvailable == true && !string.IsNullOrEmpty(context.Session.Id))
            {
                _correlationManager.SetProperty("session.id", context.Session.Id);
            }

            // Ensure correlation ID is in response headers
            context.Response.Headers["X-Correlation-Id"] = correlationContext.CorrelationId;

            await _next(context);

            // Set response properties
            _correlationManager.SetProperty("http.status_code", context.Response.StatusCode);
        }
        catch (Exception ex)
        {
            _correlationManager.SetProperty("error", true);
            _correlationManager.SetProperty("error.type", ex.GetType().Name);
            _correlationManager.SetProperty("error.message", ex.Message);
            _correlationManager.AddTag("error");

            _logger.LogError(ex, "Error in correlation context {CorrelationId}", correlationContext.CorrelationId);
            throw;
        }
        finally
        {
            _correlationManager.EndCorrelation(correlationContext.CorrelationId);
        }
    }

    private static string ExtractCorrelationId(HttpContext context)
    {
        // Try to get correlation ID from headers
        var correlationId = context.Request.Headers["X-Correlation-Id"].ToString();
        if (!string.IsNullOrEmpty(correlationId))
        {
            return correlationId;
        }

        // Try to get from query parameters
        correlationId = context.Request.Query["correlationId"].ToString();
        if (!string.IsNullOrEmpty(correlationId))
        {
            return correlationId;
        }

        // Use trace ID if available
        if (context.TraceIdentifier != null)
        {
            return context.TraceIdentifier;
        }

        // Generate new correlation ID
        return Guid.NewGuid().ToString("N")[..16];
    }
}