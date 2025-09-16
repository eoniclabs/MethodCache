using MethodCache.ETags.Middleware;
using Microsoft.AspNetCore.Builder;

namespace MethodCache.ETags.Extensions
{
    /// <summary>
    /// Extension methods for configuring ETag middleware in the application pipeline.
    /// </summary>
    public static class ApplicationBuilderExtensions
    {
        /// <summary>
        /// Adds ETag middleware to the application pipeline.
        /// This middleware should be added early in the pipeline, typically after authentication
        /// but before MVC or other content-generating middleware.
        /// </summary>
        /// <param name="app">The application builder</param>
        /// <returns>The application builder for method chaining</returns>
        public static IApplicationBuilder UseETagCaching(this IApplicationBuilder app)
        {
            return app.UseMiddleware<ETagMiddleware>();
        }

        /// <summary>
        /// Adds ETag middleware with conditional execution based on a predicate.
        /// </summary>
        /// <param name="app">The application builder</param>
        /// <param name="predicate">Predicate to determine if ETag middleware should be used</param>
        /// <returns>The application builder for method chaining</returns>
        public static IApplicationBuilder UseETagCachingWhen(this IApplicationBuilder app, Func<bool> predicate)
        {
            if (predicate())
            {
                return app.UseMiddleware<ETagMiddleware>();
            }
            return app;
        }

        /// <summary>
        /// Adds ETag middleware with environment-based conditional execution.
        /// </summary>
        /// <param name="app">The application builder</param>
        /// <param name="environments">Environments where ETag middleware should be enabled</param>
        /// <returns>The application builder for method chaining</returns>
        public static IApplicationBuilder UseETagCachingInEnvironments(this IApplicationBuilder app, params string[] environments)
        {
            return app.UseETagCachingWhen(() =>
            {
                var currentEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
                return environments.Contains(currentEnvironment, StringComparer.OrdinalIgnoreCase);
            });
        }
    }
}