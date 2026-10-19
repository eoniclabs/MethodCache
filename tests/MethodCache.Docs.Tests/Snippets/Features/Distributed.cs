using MethodCache;
using MethodCache.Extensibility;
using MethodCache.Serialization.MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Docs.Distributed
{
    public static class Setup
    {
        public static void Redis(WebApplicationBuilder builder)
        {
            // begin-snippet: distributed-redis
            builder.Services.AddMethodCache(mc => mc.UseRedis("localhost:6379"));   // MethodCache.Redis
            // end-snippet
        }

        public static void SqlServer(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: distributed-sqlserver
            builder.Services.AddMethodCache(mc => mc.UseSqlServer(connectionString));   // MethodCache.SqlServer
            // end-snippet
        }

        public static void MessagePack(WebApplicationBuilder builder)
        {
            // begin-snippet: distributed-messagepack
            builder.Services.AddMethodCache(mc => mc
                .UseRedis("localhost:6379")
                .UseMessagePack());   // MethodCache.Serialization.MessagePack
            // end-snippet
        }

        public static void MessagePackKeys(WebApplicationBuilder builder)
        {
            // begin-snippet: messagepack-key-generator
            builder.Services.AddMethodCache(cache => cache.UseMessagePack());
            // end-snippet
        }
    }
}
