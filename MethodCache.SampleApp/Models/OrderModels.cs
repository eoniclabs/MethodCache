using MethodCache.Core;
using MethodCache.Core.Runtime;

namespace MethodCache.SampleApp.Models
{
    public class Order
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public OrderStatus Status { get; set; }
        public decimal TotalAmount { get; set; }
        public List<OrderItem> Items { get; set; } = new();
        public ShippingAddress? ShippingAddress { get; set; }
    }

    public class OrderItem
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice => Quantity * UnitPrice;
    }

    public class ShippingAddress
    {
        public string Street { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string ZipCode { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
    }

    public enum OrderStatus
    {
        Pending,
        Confirmed,
        Processing,
        Shipped,
        Delivered,
        Cancelled,
        Refunded
    }

    public class OrderStatistics
    {
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public decimal AverageOrderValue { get; set; }
        public Dictionary<OrderStatus, int> OrdersByStatus { get; set; } = new();
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    public class OrderAggregation
    {
        public Dictionary<int, decimal> RevenueByProduct { get; set; } = new();
        public Dictionary<int, int> OrdersByUser { get; set; } = new();
        public Dictionary<DateTime, decimal> DailyRevenue { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public class OrderAggregationCriteria : ICacheKeyProvider
    {
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public bool IncludeProductBreakdown { get; set; }
        public bool IncludeUserBreakdown { get; set; }
        public bool IncludeDailyBreakdown { get; set; }

        public string CacheKeyPart =>
            $"from:{FromDate:yyyy-MM-dd}_to:{ToDate:yyyy-MM-dd}_products:{IncludeProductBreakdown}_users:{IncludeUserBreakdown}_daily:{IncludeDailyBreakdown}";
    }

    public class CreateOrderRequest
    {
        public List<OrderItemRequest> Items { get; set; } = new();
        public ShippingAddress ShippingAddress { get; set; } = new();
    }

    public class OrderItemRequest
    {
        public int ProductId { get; set; }
        public int Quantity { get; set; }
    }

    public class UpdateOrderRequest
    {
        public OrderStatus? Status { get; set; }
        public ShippingAddress? ShippingAddress { get; set; }
        public List<OrderItemRequest>? Items { get; set; }
    }

    public class PaymentInfo
    {
        public string CardNumber { get; set; } = string.Empty;
        public string ExpiryMonth { get; set; } = string.Empty;
        public string ExpiryYear { get; set; } = string.Empty;
        public string CVV { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    public class PaymentResult
    {
        public bool IsSuccess { get; set; }
        public string TransactionId { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
        public DateTime ProcessedAt { get; set; }
    }
}