using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using MethodCache.Extensibility;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Docs.Readme
{
    // begin-snippet: readme-quickstart-interface
    using MethodCache;

    public interface IUserService
    {
        [Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
        Task<User> GetUserAsync(int id);

        [CacheInvalidate(Tags = ["user:{id}"])]
        Task RenameAsync(int id, string name);
    }
    // end-snippet

    public sealed class UserService(IUserDatabase db) : IUserService
    {
        public Task<User> GetUserAsync(int id) => db.LoadUserAsync(id);

        public Task RenameAsync(int id, string name) => db.RenameAsync(id, name);
    }

    public static class Setup
    {
        public static void ExplicitFactory(WebApplicationBuilder builder)
        {
            // begin-snippet: explicit-factory-registration
            builder.Services.AddScoped<IUserService>(sp =>
                new UserService(sp.GetRequiredService<IUserDatabase>()));
            builder.Services.AddMethodCache();
            // end-snippet
        }

        public static void QuickStart(WebApplicationBuilder builder)
        {
            // begin-snippet: readme-quickstart-registration
            builder.Services.AddScoped<IUserService, UserService>();
            builder.Services.AddMethodCache();
            // end-snippet
        }
    }

    public sealed class HandWrittenUserService(IMemoryCache _cache, IUserDatabase _db)
    {
        // begin-snippet: readme-before
        public async Task<User> GetUserAsync(int id)
        {
            var key = $"user:{id}";
            if (_cache.TryGetValue(key, out User? user))
                return user!;

            // Two callers can both miss and both hit the database
            user = await _db.LoadUserAsync(id);
            _cache.Set(key, user, TimeSpan.FromMinutes(10));
            return user;
        }

        public async Task RenameAsync(int id, string name)
        {
            await _db.RenameAsync(id, name);
            _cache.Remove($"user:{id}");
            // ...and every other key that held this user
        }
        // end-snippet
    }

    public interface IAfter
    {
        // begin-snippet: readme-after
        [Cache(Duration = "00:10:00", Tags = ["user:{id}"])]
        Task<User> GetUserAsync(int id);

        [CacheInvalidate(Tags = ["user:{id}"])]
        Task RenameAsync(int id, string name);
        // end-snippet
    }

    public static class Invalidation
    {
        public static async Task FromCode(ICacheManager cache)
        {
            // begin-snippet: readme-invalidation
            await cache.InvalidateByTagsAsync("user:42");
            await cache.InvalidateByKeysAsync(MethodCacheKeys.For(typeof(IUserService), nameof(IUserService.GetUserAsync), MethodCacheKeys.Part(42)));
            await cache.InvalidateByTagPatternAsync("tenant:7:*");   // in-memory only
            // end-snippet
        }
    }

    public static class Configuration
    {
        public static void Layers(WebApplicationBuilder builder)
        {
            // begin-snippet: readme-configuration
            builder.Services.AddMethodCache(config =>
                config.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(5))));
            builder.Services.AddMethodCacheFromConfiguration(builder.Configuration);
            // end-snippet
        }

        public static void Distributed(WebApplicationBuilder builder)
        {
            // begin-snippet: readme-distributed
            builder.Services.AddMethodCache();
            builder.Services.AddMethodCacheRedis("localhost:6379");       // or AddMethodCacheSqlServer(connectionString)
            // end-snippet
        }

        public static void Monitoring(WebApplicationBuilder builder)
        {
            // begin-snippet: readme-monitoring
            builder.Services.AddHealthChecks().AddMethodCache();         // MethodCache.AspNetCore
            builder.Services.AddMethodCacheOpenTelemetry();               // MethodCache.OpenTelemetry
            builder.Services.AddOpenTelemetry()
                .WithTracing(t => t.AddMethodCacheInstrumentation())
                .WithMetrics(m => m.AddMethodCacheInstrumentation());

            var app = builder.Build();
            app.MapMethodCacheAdmin("CacheAdmins");                       // an authorization policy is required
            // end-snippet
        }

        public static void NativeAot(WebApplicationBuilder builder)
        {
            // begin-snippet: readme-native-aot
            builder.Services.AddSingleton<ISerializer>(new JsonCacheSerializer(AppJsonContext.Default.Options));
            // end-snippet
        }
    }

    [JsonSerializable(typeof(User))]
    public sealed partial class AppJsonContext : JsonSerializerContext;
}
