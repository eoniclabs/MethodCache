using MethodCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.Conditional
{
    public sealed record Product(int Id, string Name);

    public interface IProductService
    {
        [Cache(Duration = "00:10:00")]
        Task<List<Product>> SearchAsync(string text);

        [Cache(Duration = "00:10:00")]
        Task<Product?> FindAsync(int id);
    }

    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: conditional-cache-when
            builder.Services.AddMethodCache(config =>
            {
                // Don't remember empty search results: the next search asks the database again
                config.Method((IProductService s) => s.SearchAsync(Any<string>.Value))
                      .CacheWhen(results => results.Count > 0);

                // A product that doesn't exist yet may be created any moment
                config.Method((IProductService s) => s.FindAsync(Any<int>.Value))
                      .CacheWhen(product => product is not null);
            });
            // end-snippet
        }

        public static async Task<List<Product>> PerCall(ICacheManager cache, string text, bool preview, Func<Task<List<Product>>> search)
        {
            // begin-snippet: conditional-when
            var results = await cache.GetOrCreateAsync(
                $"search:{text}",
                async (ctx, ct) => await search(),
                o => o.WithDuration(TimeSpan.FromMinutes(10))
                      .WithEnabled(!preview));   // preview requests skip the cache entirely
            // end-snippet
            return results;
        }
    }
}
