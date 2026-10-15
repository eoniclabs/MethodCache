using System.Text.Json.Serialization;
using MethodCache;
using MethodCache.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Docs.NativeAot
{
    public sealed record Product(int Id, string Name);

    // begin-snippet: native-aot-context
    [JsonSerializable(typeof(Product))]
    [JsonSerializable(typeof(List<Product>))]   // every type a cached method returns
    public sealed partial class ShopJsonContext : JsonSerializerContext;
    // end-snippet

    public static class Setup
    {
        public static void Register(WebApplicationBuilder builder)
        {
            // begin-snippet: native-aot-registration
            builder.Services.AddMethodCache();
            builder.Services.AddMethodCacheRedis("localhost:6379");
            builder.Services.AddMethodCache(cache => cache.UseJson(ShopJsonContext.Default));
            // end-snippet
        }
    }
}
