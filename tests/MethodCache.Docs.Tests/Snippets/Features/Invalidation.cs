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

    public static class FromCode
    {
        public static async Task Tags(ICacheManager cache)
        {
            // begin-snippet: invalidation-from-code
            await cache.InvalidateTagsAsync(["order:42", "customer:7:orders"]);
            // end-snippet
        }

        public static async Task Keys(ICacheManager cache)
        {
            // begin-snippet: invalidation-by-key
            var key = MethodCacheKeys.For(typeof(IOrderService), nameof(IOrderService.GetAsync), MethodCacheKeys.Part(42));
            await cache.InvalidateKeyAsync(key);   // "Docs.Invalidation.IOrderService.GetAsync:42"
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
