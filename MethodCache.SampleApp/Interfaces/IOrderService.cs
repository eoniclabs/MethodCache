using MethodCache.Core;
using MethodCache.SampleApp.Models;
using System.Threading.Tasks;
using MethodCache.Core;

namespace MethodCache.SampleApp.Interfaces
{
    /// <summary>
    /// Order service demonstrating tag-based invalidation and complex scenarios
    /// </summary>
    public interface IOrderService
    {
        // Order retrieval with user-specific caching
        [Cache("UserOrders")]
        Task<List<Order>> GetUserOrdersAsync(int userId);

        // Order details with order-specific caching
        [Cache("OrderDetails")]
        Task<Order> GetOrderAsync(int orderId);

        // Order statistics with date-based caching
        [Cache("OrderStats")]
        Task<OrderStatistics> GetOrderStatisticsAsync(DateTime fromDate, DateTime toDate);

        // Recent orders that change frequently
        [Cache("RecentOrders")]
        Task<List<Order>> GetRecentOrdersAsync(int limit);

        // Order totals for reporting
        [Cache("OrderTotals")]
        Task<decimal> GetOrderTotalAsync(int orderId);

        // Complex aggregation that's expensive to compute
        [Cache("OrderAggregation")]
        Task<OrderAggregation> GetOrderAggregationAsync(OrderAggregationCriteria criteria);

        // Order creation (invalidates multiple caches)
        [CacheInvalidate(Tags = new[] { "UserOrders", "RecentOrders", "OrderStats", "OrderAggregation" })]
        Task<Order> CreateOrderAsync(int userId, CreateOrderRequest request);

        // Order update (invalidates order-specific and user caches)
        [CacheInvalidate(Tags = new[] { "OrderDetails", "UserOrders", "OrderTotals" })]
        Task UpdateOrderAsync(int orderId, UpdateOrderRequest request);

        // Order cancellation (invalidates multiple caches)
        [CacheInvalidate(Tags = new[] { "OrderDetails", "UserOrders", "OrderStats", "RecentOrders" })]
        Task CancelOrderAsync(int orderId);

        // Order status updates
        [CacheInvalidate(Tags = new[] { "OrderDetails", "UserOrders", "RecentOrders" })]
        Task UpdateOrderStatusAsync(int orderId, OrderStatus status);

        // Payment processing (no caching - sensitive operation)
        Task<PaymentResult> ProcessPaymentAsync(int orderId, PaymentInfo paymentInfo);

        // Audit trail (no caching - always fresh data needed)
        Task LogOrderEventAsync(int orderId, string eventType, string details);
    }
}