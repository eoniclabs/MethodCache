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

        public static void PostgreSql(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: distributed-postgresql
            builder.Services.AddMethodCache(mc => mc.UsePostgreSql(connectionString));   // MethodCache.PostgreSql
            // end-snippet
        }

        public static void InvalidationsOnly(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: distributed-invalidations-only
            builder.Services.AddMethodCache(mc => mc
                .UsePostgreSql(connectionString)
                .Distributed(o => o.StoreValuesByDefault = false));   // values stay in memory; invalidations reach every process
            // end-snippet
        }

        public static void MemoryByDefault(WebApplicationBuilder builder)
        {
            // begin-snippet: distributed-memory-by-default
            builder.Services.AddMethodCache(mc => mc
                .UseRedis("localhost:6379")
                .Distributed(o => o.StoreValuesByDefault = false));   // values stay in memory unless a method or call says otherwise
            // end-snippet
        }

        public interface ITokenClient
        {
            Task<string> RequestTokenAsync(CancellationToken cancellationToken);
        }

        public static ValueTask<string> SharedToken(ICacheManager cache, ITokenClient tokens)
        {
            // begin-snippet: distributed-shared-call
            // Every instance reuses one access token instead of asking the provider for its own
            return cache.GetOrCreateAsync("crm:access-token", async ct => await tokens.RequestTokenAsync(ct),
                o => o.WithDuration(TimeSpan.FromMinutes(50)).WithDistributed(true));
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
    }
}

namespace Docs.Distributed.WorkerAndApi
{
    using MethodCache.EntityFrameworkCore;
    using Microsoft.EntityFrameworkCore;

    public sealed class Food
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class FoodDb(DbContextOptions<FoodDb> options) : DbContext(options)
    {
        public DbSet<Food> Foods => Set<Food>();
    }

    public sealed record FoodDto(int Id, string Name);

    // begin-snippet: distributed-worker-api-reads
    public interface IFoodCatalog
    {
        // The worker writes the catalog; its saves invalidate food-catalog in every process
        [Cache(Duration = "1d", Tags = ["food-catalog"], InvalidatedElsewhere = ["food-catalog"])]
        Task<IReadOnlyList<FoodDto>> SearchAsync(string text);
    }
    // end-snippet

    public sealed class FoodCatalog(FoodDb db) : IFoodCatalog
    {
        public async Task<IReadOnlyList<FoodDto>> SearchAsync(string text)
            => await db.Foods.Where(f => f.Name.Contains(text)).Select(f => new FoodDto(f.Id, f.Name)).ToListAsync();
    }

    public static class Programs
    {
        public static void Api(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: distributed-worker-api-api
            // The API caches reads of the catalog in its own memory
            builder.Services.AddDbContext<FoodDb>(o => o.UseNpgsql(connectionString));
            builder.Services.AddScoped<IFoodCatalog, FoodCatalog>();
            builder.Services.AddMethodCache(mc => mc
                .UsePostgreSql(connectionString)
                .Distributed(o => o.StoreValuesByDefault = false));
            // end-snippet
        }

        public static void Worker(WebApplicationBuilder builder, string connectionString)
        {
            // begin-snippet: distributed-worker-api-worker
            // The worker caches nothing: its saves invalidate the tags the mapping names, in every process
            builder.Services.AddDbContext<FoodDb>(o => o.UseNpgsql(connectionString));
            builder.Services.AddMethodCache(mc => mc
                .UsePostgreSql(connectionString)
                .Distributed(o => o.StoreValuesByDefault = false)
                .UseEntityFrameworkCore<FoodDb>(ef => ef.Entity<Food>("food-catalog")));
            // end-snippet
        }
    }
}
