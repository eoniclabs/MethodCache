using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Extensions;
using MethodCache.Core.KeyGenerators;
using MethodCache.Core.Metrics;

namespace MethodCache.Examples
{
    /// <summary>
    /// Examples showcasing the new Method Chaining API that transforms MethodCache
    /// from callback-based configuration to a fluent, chainable interface.
    /// </summary>
    public class MethodChainingExamples
    {
        private readonly ICacheManager _cache;
        private readonly IUserRepository _userRepo;
        private readonly IOrderService _orderService;
        private readonly ICacheMetrics _metrics;

        public MethodChainingExamples(
            ICacheManager cache,
            IUserRepository userRepo,
            IOrderService orderService,
            ICacheMetrics metrics)
        {
            _cache = cache;
            _userRepo = userRepo;
            _orderService = orderService;
            _metrics = metrics;
        }

        /// <summary>
        /// BEFORE: Traditional callback-based configuration
        /// </summary>
        public async Task<User> GetUser_BeforeChaining(int userId)
        {
            return await _cache.GetOrCreateAsync(
                () => _userRepo.GetUserAsync(userId),
                settings: new CacheMethodSettings 
                {
                    Duration = TimeSpan.FromHours(1),
                    Tags = new[] { "user", $"user:{userId}" }
                },
                keyGenerator: new JsonKeyGenerator()
            );
        }

        /// <summary>
        /// AFTER: New method chaining API - much cleaner and more intuitive
        /// </summary>
        public async Task<User> GetUser_WithChaining(int userId)
        {
            return await _cache.Cache(() => _userRepo.GetUserAsync(userId))
                .WithDuration(TimeSpan.FromHours(1))
                .WithTags("user", $"user:{userId}")
                .WithStampedeProtection()
                .WithMetrics(_metrics)
                .WithKeyGenerator<JsonKeyGenerator>()
                .ExecuteAsync();
        }

        /// <summary>
        /// Alternative API using Build() method
        /// </summary>
        public async Task<User> GetUser_WithBuild(int userId)
        {
            return await _cache.Build(() => _userRepo.GetUserAsync(userId))
                .WithDuration(TimeSpan.FromHours(1))
                .WithTags("user", $"user:{userId}")
                .WithKeyGenerator<FastHashKeyGenerator>()
                .ExecuteAsync();
        }

        /// <summary>
        /// Complex configuration showcase
        /// </summary>
        public async Task<List<Order>> GetOrders_ComplexChaining(
            int customerId,
            OrderStatus status,
            DateTime from,
            DateTime to)
        {
            return await _cache.Cache(() => _orderService.GetOrdersAsync(customerId, status, from, to))
                .WithDuration(TimeSpan.FromMinutes(30))
                .WithSlidingExpiration(TimeSpan.FromMinutes(10))
                .WithRefreshAhead(TimeSpan.FromMinutes(5))
                .WithTags("orders", $"customer:{customerId}", $"status:{status}")
                .WithStampedeProtection(StampedeProtectionMode.Probabilistic, beta: 1.5)
                .WithDistributedLock(TimeSpan.FromSeconds(30), maxConcurrency: 2)
                .WithMetrics(_metrics)
                .WithVersion(2)
                .OnHit(ctx => Console.WriteLine($"Cache hit for key: {ctx.Key}"))
                .OnMiss(ctx => Console.WriteLine($"Cache miss for key: {ctx.Key}"))
                .When(ctx => customerId > 0) // Conditional caching
                .WithKeyGenerator<MessagePackKeyGenerator>()
                .ExecuteAsync();
        }

        /// <summary>
        /// Simple usage with minimal configuration
        /// </summary>
        public async Task<UserProfile> GetProfile_Minimal(int userId)
        {
            return await _cache.Cache(() => _userRepo.GetProfileAsync(userId))
                .WithDuration(TimeSpan.FromMinutes(15))
                .ExecuteAsync();
        }

        /// <summary>
        /// High-performance scenario with FastHashKeyGenerator
        /// </summary>
        public async Task<CriticalData> GetCriticalData_HighPerformance(int id)
        {
            return await _cache.Cache(() => _orderService.GetCriticalDataAsync(id))
                .WithDuration(TimeSpan.FromSeconds(30))
                .WithStampedeProtection() // Use defaults
                .WithKeyGenerator<FastHashKeyGenerator>() // Optimized for speed
                .ExecuteAsync();
        }

        /// <summary>
        /// Debug-friendly configuration with human-readable keys
        /// </summary>
        public async Task<ConfigData> GetConfig_DebugFriendly(string environment, string feature)
        {
            return await _cache.Cache(() => _orderService.GetConfigAsync(environment, feature))
                .WithDuration(TimeSpan.FromMinutes(10))
                .WithTags("config", $"env:{environment}", $"feature:{feature}")
                .WithKeyGenerator<JsonKeyGenerator>() // Human-readable keys
                .OnHit(ctx => Console.WriteLine($"Config cache hit: {ctx.Key}"))
                .ExecuteAsync();
        }

        /// <summary>
        /// Conditional caching based on business logic
        /// </summary>
        public async Task<AnalyticsReport> GetReport_Conditional(ReportCriteria criteria, bool isPremiumUser)
        {
            return await _cache.Cache(() => _orderService.GenerateReportAsync(criteria))
                .WithDuration(isPremiumUser ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30))
                .WithTags("analytics", isPremiumUser ? "premium" : "standard")
                .When(ctx => criteria.IsExpensive) // Only cache expensive reports
                .WithKeyGenerator<MessagePackKeyGenerator>()
                .ExecuteAsync();
        }

        /// <summary>
        /// Background refresh for frequently accessed data
        /// </summary>
        public async Task<List<PopularItem>> GetPopularItems_BackgroundRefresh()
        {
            return await _cache.Cache(() => _orderService.GetPopularItemsAsync())
                .WithDuration(TimeSpan.FromHours(1))
                .WithRefreshAhead(TimeSpan.FromMinutes(50)) // Refresh 10 min before expiry
                .WithTags("popular-items")
                .WithMetrics(_metrics)
                .ExecuteAsync();
        }

        /// <summary>
        /// Multiple key generators based on data characteristics
        /// </summary>
        public async Task<DataResult> GetData_DynamicKeyGenerator(QueryRequest request)
        {
            var builder = _cache.Cache(() => _orderService.ProcessQueryAsync(request))
                .WithDuration(TimeSpan.FromMinutes(30))
                .WithTags("queries");

            // Choose key generator based on request complexity
            if (request.Parameters.Count > 10)
            {
                // Complex requests: Use MessagePack for binary efficiency
                builder = builder.WithKeyGenerator<MessagePackKeyGenerator>();
            }
            else if (request.IsDebugMode)
            {
                // Debug mode: Use JSON for human readability
                builder = builder.WithKeyGenerator<JsonKeyGenerator>();
            }
            else
            {
                // Normal requests: Use FastHash for performance
                builder = builder.WithKeyGenerator<FastHashKeyGenerator>();
            }

            return await builder.ExecuteAsync();
        }

        /// <summary>
        /// Comparison of all three patterns side by side
        /// </summary>
        public class ApiPatternComparison
        {
            private readonly ICacheManager _cache;
            private readonly IDataService _dataService;

            public ApiPatternComparison(ICacheManager cache, IDataService dataService)
            {
                _cache = cache;
                _dataService = dataService;
            }

            // Pattern 1: Current callback-based API (still supported)
            public async Task<Data> GetData_CallbackBased(int id)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.GetDataAsync(id),
                    opts => opts
                        .WithDuration(TimeSpan.FromHours(1))
                        .WithTags("data"),
                    keyGenerator: new JsonKeyGenerator()
                );
            }

            // Pattern 2: NEW Method Chaining API - Cache()
            public async Task<Data> GetData_MethodChaining(int id)
            {
                return await _cache.Cache(() => _dataService.GetDataAsync(id))
                    .WithDuration(TimeSpan.FromHours(1))
                    .WithTags("data")
                    .WithKeyGenerator<JsonKeyGenerator>()
                    .ExecuteAsync();
            }

            // Pattern 3: NEW Method Chaining API - Build()
            public async Task<Data> GetData_BuildPattern(int id)
            {
                return await _cache.Build(() => _dataService.GetDataAsync(id))
                    .WithDuration(TimeSpan.FromHours(1))
                    .WithTags("data")
                    .WithKeyGenerator<JsonKeyGenerator>()
                    .ExecuteAsync();
            }
        }

        /// <summary>
        /// Real-world scenarios showing the power of method chaining
        /// </summary>
        public class RealWorldScenarios
        {
            private readonly ICacheManager _cache;
            private readonly IUserService _userService;
            private readonly INotificationService _notificationService;

            public RealWorldScenarios(
                ICacheManager cache,
                IUserService userService,
                INotificationService notificationService)
            {
                _cache = cache;
                _userService = userService;
                _notificationService = notificationService;
            }

            /// <summary>
            /// E-commerce: User's shopping cart with real-time updates
            /// </summary>
            public async Task<ShoppingCart> GetShoppingCart(int userId)
            {
                return await _cache.Cache(() => _userService.GetShoppingCartAsync(userId))
                    .WithDuration(TimeSpan.FromMinutes(30))
                    .WithTags("cart", $"user:{userId}")
                    .WithStampedeProtection() // Prevent duplicate cart loading
                    .OnHit(ctx => _notificationService.LogCacheHit("shopping_cart", userId))
                    .OnMiss(ctx => _notificationService.LogCacheMiss("shopping_cart", userId))
                    .ExecuteAsync();
            }

            /// <summary>
            /// Analytics: Expensive report generation with smart caching
            /// </summary>
            public async Task<SalesReport> GenerateSalesReport(
                DateTime from,
                DateTime to,
                string region,
                bool isRealTimeRequired)
            {
                return await _cache.Cache(() => _userService.GenerateSalesReportAsync(from, to, region))
                    .WithDuration(isRealTimeRequired ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(2))
                    .WithRefreshAhead(TimeSpan.FromMinutes(isRealTimeRequired ? 2 : 30))
                    .WithTags("sales-report", $"region:{region}")
                    .WithDistributedLock(TimeSpan.FromMinutes(2)) // Prevent concurrent generation
                    .When(ctx => (to - from).TotalDays >= 1) // Only cache multi-day reports
                    .WithKeyGenerator<MessagePackKeyGenerator>()
                    .ExecuteAsync();
            }

            /// <summary>
            /// User authentication: Profile data with tiered caching
            /// </summary>
            public async Task<UserProfile> GetUserProfile(int userId, string role)
            {
                var duration = role switch
                {
                    "Admin" => TimeSpan.FromMinutes(5),      // Admins get fresh data
                    "Premium" => TimeSpan.FromMinutes(15),   // Premium users get frequent updates
                    _ => TimeSpan.FromHours(1)               // Regular users get longer cache
                };

                return await _cache.Cache(() => _userService.GetProfileAsync(userId))
                    .WithDuration(duration)
                    .WithSlidingExpiration(TimeSpan.FromMinutes(30))
                    .WithTags("user-profile", $"user:{userId}", $"role:{role}")
                    .WithMetrics(_cache.Services.GetService<ICacheMetrics>())
                    .ExecuteAsync();
            }
        }
    }

    // Supporting interfaces and types
    public interface IUserRepository
    {
        Task<User> GetUserAsync(int userId);
        Task<UserProfile> GetProfileAsync(int userId);
    }

    public interface IOrderService
    {
        Task<List<Order>> GetOrdersAsync(int customerId, OrderStatus status, DateTime from, DateTime to);
        Task<CriticalData> GetCriticalDataAsync(int id);
        Task<ConfigData> GetConfigAsync(string environment, string feature);
        Task<AnalyticsReport> GenerateReportAsync(ReportCriteria criteria);
        Task<List<PopularItem>> GetPopularItemsAsync();
        Task<DataResult> ProcessQueryAsync(QueryRequest request);
    }

    public interface IDataService
    {
        Task<Data> GetDataAsync(int id);
    }

    public interface IUserService
    {
        Task<ShoppingCart> GetShoppingCartAsync(int userId);
        Task<SalesReport> GenerateSalesReportAsync(DateTime from, DateTime to, string region);
        Task<UserProfile> GetProfileAsync(int userId);
    }

    public interface INotificationService
    {
        void LogCacheHit(string operation, int userId);
        void LogCacheMiss(string operation, int userId);
    }

    // Data types
    public class User { }
    public class UserProfile { }
    public class Order { }
    public class CriticalData { }
    public class ConfigData { }
    public class AnalyticsReport { }
    public class PopularItem { }
    public class DataResult { }
    public class Data { }
    public class ShoppingCart { }
    public class SalesReport { }

    public class ReportCriteria
    {
        public bool IsExpensive { get; set; }
    }

    public class QueryRequest
    {
        public Dictionary<string, object> Parameters { get; set; } = new();
        public bool IsDebugMode { get; set; }
    }

    public enum OrderStatus
    {
        Pending,
        Active,
        Completed
    }

    public enum StampedeProtectionMode
    {
        Probabilistic
    }
}