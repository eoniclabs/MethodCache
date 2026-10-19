using System.Collections.Concurrent;
using MethodCache;
using MethodCache.HttpCaching;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using IUserService = Docs.Readme.IUserService;
using UserService = Docs.Readme.UserService;

namespace Docs.Packages
{
    public interface IGitHubClient;

    public sealed class GitHubClient(HttpClient http) : IGitHubClient
    {
        public HttpClient Http { get; } = http;
    }

    public interface IWeatherClient;

    public sealed class WeatherClient(HttpClient http) : IWeatherClient
    {
        public HttpClient Http { get; } = http;
    }

    public static class Http
    {
        public static void Basic(WebApplicationBuilder builder)
        {
            // begin-snippet: http-basic
            builder.Services.AddMethodCache();
            builder.Services.AddHttpClient<IGitHubClient, GitHubClient>()
                .AddHttpCaching();
            // end-snippet
        }

        public static void Settings(WebApplicationBuilder builder)
        {
            // begin-snippet: http-settings
            builder.Services.AddHttpClient<IWeatherClient, WeatherClient>()
                .AddHttpCaching(options =>
                {
                    options.EnableStaleIfError = true;           // serve the last response when the API is down
                    options.EnableStaleWhileRevalidate = true;   // when the server sends stale-while-revalidate
                    options.AddDiagnosticHeaders = true;         // X-Cache: HIT, MISS, REVALIDATED, STALE or STALE-IF-ERROR
                });
            // end-snippet
        }

        public static void Storage(WebApplicationBuilder builder)
        {
            // begin-snippet: http-storage-size
            builder.Services.AddMethodCache(cache => cache.Memory(o => o.MaxHttpBytes = 50 * 1024 * 1024)); // default 100 MB
            // end-snippet
        }

        public static void Shared(WebApplicationBuilder builder)
        {
            // begin-snippet: http-distributed
            builder.Services.AddMethodCache(cache => cache.UseRedis("localhost:6379"));
            // end-snippet
        }

    }

    public static class Redis
    {
        public static void Basic(WebApplicationBuilder builder)
        {
            // begin-snippet: redis-basic
            builder.Services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));
            // end-snippet
        }

        public static void Options(WebApplicationBuilder builder)
        {
            // begin-snippet: redis-options
            builder.Services.AddMethodCache(mc => mc
                .UseRedis(options => options.Configuration = "redis1:6379,redis2:6379,password=...")
                .Distributed(options => options.KeyPrefix = "orders-api:"));   // data and backplane use the same final prefix
            // end-snippet
        }

        public static void SharedConnection(WebApplicationBuilder builder)
        {
            // begin-snippet: redis-shared-connection
            builder.Services.AddMethodCache(mc => mc.UseRedis(options =>
                options.ConnectionMultiplexerFactory = sp => sp.GetRequiredService<IConnectionMultiplexer>()));
            // end-snippet
        }

        public static void Outage(WebApplicationBuilder builder)
        {
            // begin-snippet: distributed-outage-options
            builder.Services.AddMethodCache(mc => mc.Distributed(options =>
            {
                options.OperationTimeout = TimeSpan.FromMilliseconds(500);
                options.CircuitBreakerDuration = TimeSpan.FromSeconds(30);
            }));
            // end-snippet
        }
    }

    public static class SqlServer
    {
        public static void Basic(WebApplicationBuilder builder)
        {
            // begin-snippet: sqlserver-basic
            builder.Services.AddMethodCache(mc => mc.UseSqlServer(builder.Configuration.GetConnectionString("Cache")!));
            // end-snippet
        }

        public static void Options(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: sqlserver-options
            builder.Services.AddMethodCache(mc => mc.UseSqlServer(options =>
            {
                options.ConnectionString = connectionString;
                options.SchemaName = "cache";
                options.BackplanePollingInterval = TimeSpan.FromMilliseconds(500);
            }));
            // end-snippet
        }
    }

    public static class AspNetCore
    {
        public static void Setup(WebApplicationBuilder builder)
        {
            // begin-snippet: aspnetcore-setup
            builder.Services.AddScoped<IUserService, UserService>();
            builder.Services.AddMethodCache();
            builder.Services.AddHealthChecks().AddMethodCache();
            builder.Services.AddAuthorization(o => o.AddPolicy("CacheAdmins", p => p.RequireRole("Admin")));

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();

            app.MapMethodCacheAdmin("CacheAdmins");
            app.MapHealthChecks("/health");
            // end-snippet
        }
    }

    public static class Telemetry
    {
        public static void Setup(WebApplicationBuilder builder)
        {
            // begin-snippet: otel-setup
            builder.Services.AddMethodCache();

            builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation()
                    .AddMethodCacheInstrumentation()
                    .AddOtlpExporter())
                .WithMetrics(metrics => metrics
                    .AddMethodCacheInstrumentation()
                    .AddOtlpExporter());
            // end-snippet
        }

        public static void IdentityOptions(WebApplicationBuilder builder)
        {
            // begin-snippet: otel-baggage
            builder.Services.AddMethodCache(mc => mc.Telemetry(options =>
            {
                options.RecordCacheKeys = true; // hashes by default; raw keys can contain secrets
                options.RecordCacheTags = false;
            }));
            // end-snippet
        }

    }
}
