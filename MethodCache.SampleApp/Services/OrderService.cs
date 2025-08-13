using MethodCache.SampleApp.Interfaces;
using MethodCache.SampleApp.Models;

namespace MethodCache.SampleApp.Services
{
    public class OrderService : IOrderService
    {
        private readonly List<Order> _orders;
        private readonly Random _random = new();

        public OrderService()
        {
            _orders = GenerateSampleOrders();
        }

        public async Task<Order?> GetOrderByIdAsync(int orderId)
        {
            // Simulate database lookup
            await Task.Delay(_random.Next(50, 150));
            
            Console.WriteLine($"[OrderService] Fetching order {orderId} from database...");
            return _orders.FirstOrDefault(o => o.Id == orderId);
        }

        public async Task<List<Order>> GetOrdersByUserIdAsync(int userId)
        {
            // Simulate user orders lookup
            await Task.Delay(_random.Next(100, 250));
            
            Console.WriteLine($"[OrderService] Loading orders for user {userId}...");
            return _orders.Where(o => o.UserId == userId).OrderByDescending(o => o.CreatedAt).ToList();
        }

        public async Task<OrderStatistics> GetOrderStatisticsAsync(DateTime fromDate, DateTime toDate)
        {
            // Simulate complex statistics calculation
            await Task.Delay(_random.Next(300, 700));
            
            Console.WriteLine($"[OrderService] Calculating order statistics from {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd}...");
            
            var relevantOrders = _orders.Where(o => o.CreatedAt >= fromDate && o.CreatedAt <= toDate).ToList();
            
            var statistics = new OrderStatistics
            {
                FromDate = fromDate,
                ToDate = toDate,
                TotalOrders = relevantOrders.Count,
                TotalRevenue = relevantOrders.Sum(o => o.TotalAmount),
                AverageOrderValue = relevantOrders.Any() ? relevantOrders.Average(o => o.TotalAmount) : 0,
                OrdersByStatus = relevantOrders.GroupBy(o => o.Status).ToDictionary(g => g.Key, g => g.Count()),
                GeneratedAt = DateTime.UtcNow
            };
            
            Console.WriteLine($"[OrderService] Statistics calculated: {statistics.TotalOrders} orders, ${statistics.TotalRevenue:F2} revenue");
            return statistics;
        }

        public async Task<OrderAggregation> GetOrderAggregationAsync(OrderAggregationCriteria criteria)
        {
            // Simulate complex aggregation processing
            await Task.Delay(_random.Next(400, 900));
            
            Console.WriteLine($"[OrderService] Processing order aggregation: {criteria.CacheKeyPart}...");
            
            var relevantOrders = _orders.Where(o => o.CreatedAt >= criteria.FromDate && o.CreatedAt <= criteria.ToDate).ToList();
            
            var aggregation = new OrderAggregation
            {
                GeneratedAt = DateTime.UtcNow
            };

            if (criteria.IncludeProductBreakdown)
            {
                aggregation.RevenueByProduct = relevantOrders
                    .SelectMany(o => o.Items)
                    .GroupBy(i => i.ProductId)
                    .ToDictionary(g => g.Key, g => g.Sum(i => i.TotalPrice));
            }

            if (criteria.IncludeUserBreakdown)
            {
                aggregation.OrdersByUser = relevantOrders
                    .GroupBy(o => o.UserId)
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            if (criteria.IncludeDailyBreakdown)
            {
                aggregation.DailyRevenue = relevantOrders
                    .GroupBy(o => o.CreatedAt.Date)
                    .ToDictionary(g => g.Key, g => g.Sum(o => o.TotalAmount));
            }
            
            Console.WriteLine($"[OrderService] Aggregation complete for {relevantOrders.Count} orders");
            return aggregation;
        }

        public async Task<List<Order>> GetRecentOrdersAsync(int count)
        {
            // Simulate recent orders query
            await Task.Delay(_random.Next(100, 200));
            
            Console.WriteLine($"[OrderService] Fetching {count} most recent orders...");
            
            return _orders
                .OrderByDescending(o => o.CreatedAt)
                .Take(count)
                .ToList();
        }

        public async Task<List<Order>> GetOrdersByStatusAsync(OrderStatus status)
        {
            // Simulate status-based query
            await Task.Delay(_random.Next(100, 250));
            
            Console.WriteLine($"[OrderService] Loading orders with status: {status}...");
            
            return _orders.Where(o => o.Status == status).OrderByDescending(o => o.CreatedAt).ToList();
        }

        public async Task<Order> CreateOrderAsync(CreateOrderRequest request, int userId)
        {
            // Simulate order creation with payment processing
            await Task.Delay(_random.Next(300, 600));
            
            Console.WriteLine($"[OrderService] Creating new order for user {userId}...");
            
            var newOrder = new Order
            {
                Id = _orders.Max(o => o.Id) + 1,
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                Status = OrderStatus.Pending,
                ShippingAddress = request.ShippingAddress,
                Items = request.Items.Select(item => new OrderItem
                {
                    ProductId = item.ProductId,
                    ProductName = $"Product {item.ProductId}",
                    Quantity = item.Quantity,
                    UnitPrice = Math.Round((decimal)(_random.NextDouble() * 100 + 10), 2)
                }).ToList()
            };
            
            newOrder.TotalAmount = newOrder.Items.Sum(i => i.TotalPrice);
            _orders.Add(newOrder);
            
            Console.WriteLine($"[OrderService] Order {newOrder.Id} created with total ${newOrder.TotalAmount:F2}");
            return newOrder;
        }

        // Additional interface methods
        public async Task<Order> GetOrderAsync(int orderId)
        {
            return await GetOrderByIdAsync(orderId) ?? new Order();
        }
        
        public async Task<List<Order>> GetUserOrdersAsync(int userId)
        {
            return await GetOrdersByUserIdAsync(userId);
        }
        
        public async Task<decimal> GetOrderTotalAsync(int orderId)
        {
            await Task.Delay(_random.Next(50, 150));
            var order = await GetOrderByIdAsync(orderId);
            return order?.TotalAmount ?? 0;
        }
        
        public async Task<Order> CreateOrderAsync(int userId, CreateOrderRequest request)
        {
            return await CreateOrderAsync(request, userId);
        }
        
        public async Task UpdateOrderStatusAsync(int orderId, OrderStatus status)
        {
            await Task.Delay(_random.Next(100, 200));
            Console.WriteLine($"[OrderService] Updating order {orderId} status to {status}");
            
            var order = _orders.FirstOrDefault(o => o.Id == orderId);
            if (order != null)
            {
                order.Status = status;
                order.UpdatedAt = DateTime.UtcNow;
            }
        }
        
        public async Task LogOrderEventAsync(int orderId, string eventType, string details)
        {
            await Task.Delay(_random.Next(50, 100));
            Console.WriteLine($"[OrderService] Logging event for order {orderId}: {eventType} - {details}");
        }

        public async Task UpdateOrderAsync(int orderId, UpdateOrderRequest request)
        {
            // Simulate order update
            await Task.Delay(_random.Next(200, 400));
            
            Console.WriteLine($"[OrderService] Updating order {orderId}...");
            
            var order = _orders.FirstOrDefault(o => o.Id == orderId);
            if (order == null)
                throw new ArgumentException($"Order {orderId} not found");

            if (request.Status.HasValue)
                order.Status = request.Status.Value;
                
            if (request.ShippingAddress != null)
                order.ShippingAddress = request.ShippingAddress;
                
            order.UpdatedAt = DateTime.UtcNow;
            
            Console.WriteLine($"[OrderService] Order {orderId} updated successfully");
        }

        public async Task CancelOrderAsync(int orderId)
        {
            // Simulate order cancellation
            await Task.Delay(_random.Next(150, 300));
            
            Console.WriteLine($"[OrderService] Cancelling order {orderId}...");
            
            var order = _orders.FirstOrDefault(o => o.Id == orderId);
            if (order != null)
            {
                order.Status = OrderStatus.Cancelled;
                order.UpdatedAt = DateTime.UtcNow;
            }
        }

        public async Task<PaymentResult> ProcessPaymentAsync(int orderId, PaymentInfo paymentInfo)
        {
            // Simulate payment processing
            await Task.Delay(_random.Next(1000, 2000));
            
            Console.WriteLine($"[OrderService] Processing payment for order {orderId}...");
            
            // Simulate payment success/failure
            var isSuccess = _random.NextDouble() > 0.1; // 90% success rate
            
            var result = new PaymentResult
            {
                IsSuccess = isSuccess,
                TransactionId = isSuccess ? Guid.NewGuid().ToString() : string.Empty,
                ErrorMessage = isSuccess ? null : "Payment processing failed",
                ProcessedAt = DateTime.UtcNow
            };
            
            if (isSuccess)
            {
                var order = _orders.FirstOrDefault(o => o.Id == orderId);
                if (order != null)
                {
                    order.Status = OrderStatus.Confirmed;
                    order.UpdatedAt = DateTime.UtcNow;
                }
            }
            
            Console.WriteLine($"[OrderService] Payment {(isSuccess ? "successful" : "failed")} for order {orderId}");
            return result;
        }

        private List<Order> GenerateSampleOrders()
        {
            var orders = new List<Order>();
            var statuses = Enum.GetValues<OrderStatus>();
            
            for (int i = 1; i <= 300; i++)
            {
                var itemCount = _random.Next(1, 5);
                var items = new List<OrderItem>();
                
                for (int j = 0; j < itemCount; j++)
                {
                    items.Add(new OrderItem
                    {
                        ProductId = _random.Next(1, 201),
                        ProductName = $"Product {_random.Next(1, 201)}",
                        Quantity = _random.Next(1, 4),
                        UnitPrice = Math.Round((decimal)(_random.NextDouble() * 100 + 10), 2)
                    });
                }
                
                var order = new Order
                {
                    Id = i,
                    UserId = _random.Next(1, 101),
                    CreatedAt = DateTime.UtcNow.AddDays(-_random.Next(1, 180)),
                    Status = statuses[_random.Next(statuses.Length)],
                    Items = items,
                    ShippingAddress = new ShippingAddress
                    {
                        Street = $"{_random.Next(100, 9999)} Main St",
                        City = "Sample City",
                        State = "CA",
                        ZipCode = $"{_random.Next(10000, 99999)}",
                        Country = "USA"
                    }
                };
                
                order.TotalAmount = order.Items.Sum(i => i.TotalPrice);
                orders.Add(order);
            }
            
            return orders;
        }
    }
}
