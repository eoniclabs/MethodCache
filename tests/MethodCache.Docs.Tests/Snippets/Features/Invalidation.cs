using MethodCache;

namespace Docs.Invalidation
{
    public sealed record Order(int Id, int CustomerId, decimal Total);

    // begin-snippet: invalidation-tags
    public interface IOrderService
    {
        [Cache(Duration = "00:10:00", Tags = ["orders", "order:{orderId}"])]
        Task<Order> GetAsync(int orderId);

        [Cache(Duration = "00:05:00", Tags = ["orders", "customer:{customerId}:orders", "customer:{customerId}"])]
        Task<List<Order>> ListForCustomerAsync(int customerId);

        // Removes this order and the customer's list, nothing else
        [CacheInvalidate(Tags = ["order:{orderId}", "customer:{customerId}:orders", "customer:{customerId}"])]
        Task CancelAsync(int orderId, int customerId);

        // Removes every cached order
        [CacheInvalidate(Tags = ["orders"])]
        Task ImportAsync(Stream file);
    }
    // end-snippet

    // begin-snippet: invalidation-elsewhere-interface
    public interface IPriceSource
    {
        const string RateTag = "price:{provider}:{model}";

        [Cache(Duration = "00:05:00", Tags = [RateTag])]
        Task<decimal?> GetRateAsync(string provider, string model);
    }
    // end-snippet

    public sealed class PriceDb
    {
        public Dictionary<(string, string), decimal> Rates { get; } = [];

        public Task SaveRateAsync(string provider, string model, decimal rate)
        {
            Rates[(provider, model)] = rate;
            return Task.CompletedTask;
        }
    }

    public static class PriceEndpoints
    {
        // begin-snippet: invalidation-elsewhere
        public static async Task UpdateRateAsync(string provider, string model, decimal rate,
            PriceDb db, ICacheManager cache, IMethodCacheKeys keys)
        {
            await db.SaveRateAsync(provider, model, rate);   // written directly, not through IPriceSource

            // The tag GetRateAsync(provider, model) stored its result under, formatted exactly as the decorator does
            await cache.InvalidateTagAsync(
                keys.Tag<IPriceSource>(s => s.GetRateAsync(provider, model), IPriceSource.RateTag));
        }
        // end-snippet
    }

    public static class FromCode
    {
        public static async Task Tags(ICacheManager cache)
        {
            // begin-snippet: invalidation-from-code
            await cache.InvalidateTagsAsync(["order:42", "customer:7:orders"]);
            // end-snippet
        }

        public static async Task Keys(ICacheManager cache, IMethodCacheKeys keys)
        {
            // begin-snippet: invalidation-by-key
            var key = keys.For<IOrderService>(orders => orders.GetAsync(42));
            await cache.InvalidateKeyAsync(key);   // "[MyApp]Docs.Invalidation.IOrderService.GetAsync:42"
            // end-snippet
        }

        public static async Task Pattern(ICacheManager cache)
        {
            // begin-snippet: invalidation-pattern
            await cache.InvalidateTagAsync("customer:7");   // add this tag to every customer entry
            // end-snippet
        }
    }
}
