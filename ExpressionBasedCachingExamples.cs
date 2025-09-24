using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Extensions;

namespace MethodCache.Examples
{
    /// <summary>
    /// Examples showing how expression-based caching simplifies the developer experience.
    /// </summary>
    public class ExpressionBasedCachingExamples
    {
        private readonly ICacheManager _cacheManager;
        private readonly IUserRepository _userRepository;
        private readonly IOrderService _orderService;
        private readonly IConfigurationService _configService;

        public ExpressionBasedCachingExamples(
            ICacheManager cacheManager,
            IUserRepository userRepository,
            IOrderService orderService,
            IConfigurationService configService)
        {
            _cacheManager = cacheManager;
            _userRepository = userRepository;
            _orderService = orderService;
            _configService = configService;
        }

        /// <summary>
        /// Simple user caching with automatic key generation.
        /// Generated key: "UserRepository.GetUserAsync:userId:123"
        /// </summary>
        public async Task<User> GetUserExample(int userId)
        {
            return await _cacheManager.GetOrCreateAsync(
                () => _userRepository.GetUserAsync(userId),
                opts => opts.WithDuration(TimeSpan.FromHours(1))
            );
        }

        /// <summary>
        /// Multi-parameter method with automatic key generation.
        /// Generated key: "OrderService.GetOrdersByCustomerAsync:customerId:123:status:Active:includeItems:true"
        /// </summary>
        public async Task<List<Order>> GetOrdersExample(int customerId, OrderStatus status, bool includeItems)
        {
            return await _cacheManager.GetOrCreateAsync(
                () => _orderService.GetOrdersByCustomerAsync(customerId, status, includeItems),
                opts => opts
                    .WithDuration(TimeSpan.FromMinutes(30))
                    .WithTags("orders", $"customer:{customerId}")
            );
        }

        /// <summary>
        /// Synchronous method caching.
        /// Generated key: "ConfigurationService.GetSettings:section:AppSettings"
        /// </summary>
        public async Task<AppSettings> GetConfigurationExample(string section)
        {
            return await _cacheManager.GetOrCreateAsync(
                () => _configService.GetSettings(section),
                opts => opts
                    .WithDuration(TimeSpan.FromMinutes(15))
                    .WithTags("configuration")
            );
        }

        /// <summary>
        /// Complex object parameter example.
        /// Generated key: "OrderService.SearchOrdersAsync:criteria:OrderSearchCriteria..."
        /// </summary>
        public async Task<List<Order>> SearchOrdersExample(OrderSearchCriteria criteria)
        {
            return await _cacheManager.GetOrCreateAsync(
                () => _orderService.SearchOrdersAsync(criteria),
                opts => opts
                    .WithDuration(TimeSpan.FromMinutes(10))
                    .WithTags("orders", "search")
            );
        }

        /// <summary>
        /// Custom key generator for scenarios with very long parameter lists.
        /// </summary>
        public async Task<AnalyticsReport> GetAnalyticsWithCustomKeyGenerator(DateTime from, DateTime to, List<string> metrics)
        {
            var hashKeyGenerator = new HashBasedExpressionKeyGenerator(
                new FastHashKeyGenerator());

            return await _cacheManager.GetOrCreateAsync(
                () => _orderService.GenerateAnalyticsReportAsync(from, to, metrics),
                hashKeyGenerator,
                opts => opts
                    .WithDuration(TimeSpan.FromHours(6))
                    .WithTags("analytics", "reports")
            );
        }

        /// <summary>
        /// Comparison: Before and After
        /// </summary>
        public class BeforeAndAfterComparison
        {
            private readonly ICacheManager _cache;
            private readonly IUserRepository _userRepo;

            public BeforeAndAfterComparison(ICacheManager cache, IUserRepository userRepo)
            {
                _cache = cache;
                _userRepo = userRepo;
            }

            // BEFORE: Manual key management (current approach)
            public async Task<User> GetUser_ManualKey(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    key: $"user:{userId}",  // Manual key - error prone, not refactoring safe
                    factory: (ctx, ct) => _userRepo.GetUserAsync(userId),
                    configure: opts => opts.WithDuration(TimeSpan.FromHours(1))
                );
            }

            // AFTER: Automatic key generation (new approach)
            public async Task<User> GetUser_AutomaticKey(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    () => _userRepo.GetUserAsync(userId),  // Expression - refactoring safe, no manual keys
                    opts => opts.WithDuration(TimeSpan.FromHours(1))
                );
            }

            // BEFORE: Complex multi-parameter scenario
            public async Task<List<Order>> GetOrders_ManualKey(int customerId, OrderStatus status, DateTime from, DateTime to)
            {
                // Error-prone manual key construction
                var key = $"orders:customer:{customerId}:status:{status}:from:{from:yyyyMMdd}:to:{to:yyyyMMdd}";

                return await _cache.GetOrCreateAsync(
                    key: key,
                    factory: (ctx, ct) => _userRepo.GetOrdersAsync(customerId, status, from, to),
                    configure: opts => opts.WithDuration(TimeSpan.FromMinutes(30))
                );
            }

            // AFTER: Automatic key generation handles complexity
            public async Task<List<Order>> GetOrders_AutomaticKey(int customerId, OrderStatus status, DateTime from, DateTime to)
            {
                return await _cache.GetOrCreateAsync(
                    () => _userRepo.GetOrdersAsync(customerId, status, from, to),  // All parameters handled automatically
                    opts => opts.WithDuration(TimeSpan.FromMinutes(30))
                );
            }
        }

        /// <summary>
        /// Advanced scenarios showing the flexibility of the system.
        /// </summary>
        public class AdvancedScenarios
        {
            private readonly ICacheManager _cache;
            private readonly IDataService _dataService;

            public AdvancedScenarios(ICacheManager cache, IDataService dataService)
            {
                _cache = cache;
                _dataService = dataService;
            }

            /// <summary>
            /// Conditional caching based on parameter values.
            /// </summary>
            public async Task<DataResult> GetDataConditionally(string query, bool useCache = true)
            {
                if (!useCache)
                {
                    // Skip caching entirely
                    return await _dataService.QueryDataAsync(query);
                }

                return await _cache.GetOrCreateAsync(
                    () => _dataService.QueryDataAsync(query),
                    opts => opts
                        .WithDuration(TimeSpan.FromMinutes(30))
                        .When(ctx => query.Length > 10) // Only cache complex queries
                );
            }

            /// <summary>
            /// Different cache durations based on method parameters.
            /// </summary>
            public async Task<WeatherData> GetWeatherData(string location, bool isPremiumUser)
            {
                var cacheDuration = isPremiumUser
                    ? TimeSpan.FromMinutes(5)   // Premium users get fresher data
                    : TimeSpan.FromMinutes(30); // Free users get cached data longer

                return await _cache.GetOrCreateAsync(
                    () => _dataService.GetWeatherAsync(location),
                    opts => opts
                        .WithDuration(cacheDuration)
                        .WithTags("weather", $"location:{location}")
                );
            }

            /// <summary>
            /// Versioned caching for API responses.
            /// </summary>
            public async Task<ApiResponse> GetApiData(string endpoint, int apiVersion)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.CallApiAsync(endpoint),
                    opts => opts
                        .WithDuration(TimeSpan.FromHours(1))
                        .WithVersion(apiVersion) // Automatic cache invalidation when API version changes
                        .WithTags("api", endpoint)
                );
            }
        }
    }

    // Supporting types for examples
    public interface IUserRepository
    {
        Task<User> GetUserAsync(int userId);
        Task<List<Order>> GetOrdersAsync(int customerId, OrderStatus status, DateTime from, DateTime to);
    }

    public interface IOrderService
    {
        Task<List<Order>> GetOrdersByCustomerAsync(int customerId, OrderStatus status, bool includeItems);
        Task<List<Order>> SearchOrdersAsync(OrderSearchCriteria criteria);
        Task<AnalyticsReport> GenerateAnalyticsReportAsync(DateTime from, DateTime to, List<string> metrics);
    }

    public interface IConfigurationService
    {
        AppSettings GetSettings(string section);
    }

    public interface IDataService
    {
        Task<DataResult> QueryDataAsync(string query);
        Task<WeatherData> GetWeatherAsync(string location);
        Task<ApiResponse> CallApiAsync(string endpoint);
    }

    public class User { }
    public class Order { }
    public class AppSettings { }
    public class AnalyticsReport { }
    public class OrderSearchCriteria { }
    public class DataResult { }
    public class WeatherData { }
    public class ApiResponse { }

    public enum OrderStatus
    {
        Pending,
        Active,
        Completed,
        Cancelled
    }
}