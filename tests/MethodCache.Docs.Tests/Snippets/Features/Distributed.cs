using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Docs.Distributed
{
    public static class Setup
    {
        public static void Redis(WebApplicationBuilder builder)
        {
            // begin-snippet: distributed-redis
            builder.Services.AddMethodCache();
            builder.Services.AddMethodCacheRedis("localhost:6379");   // MethodCache.Redis
            // end-snippet
        }

        public static void SqlServer(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: distributed-sqlserver
            builder.Services.AddMethodCache();
            builder.Services.AddMethodCacheSqlServer(connectionString);   // MethodCache.SqlServer
            // end-snippet
        }

        public static void Custom(WebApplicationBuilder builder, MethodCache.Extensibility.IAtomicInvalidationStore customCache)
        {
            // begin-snippet: distributed-custom
            builder.Services.AddMethodCache();
            builder.Services.AddMethodCacheDistributedCache(
                _ => customCache);   // IDistributedCache with backend-atomic invalidation stamps
            // end-snippet
        }

        public static void MessagePack(WebApplicationBuilder builder)
        {
            // begin-snippet: distributed-messagepack
            builder.Services.AddMethodCacheRedis("localhost:6379");
            builder.Services.AddMethodCacheMessagePack();   // MethodCache.Serialization.MessagePack
            // end-snippet
        }
    }
}
