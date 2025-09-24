using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MethodCache.Core;
using MethodCache.Core.Extensions;

namespace MethodCache.Examples
{
    /// <summary>
    /// Examples showing the dramatically simplified Tier 2 API.
    /// This API is now as simple as FluentCache but leverages MethodCache's sophisticated key generators.
    /// </summary>
    public class SimplifiedApiExamples
    {
        private readonly ICacheManager _cache;
        private readonly IUserRepository _userRepo;
        private readonly IOrderService _orderService;
        private readonly IConfigService _configService;

        public SimplifiedApiExamples(
            ICacheManager cache,
            IUserRepository userRepo,
            IOrderService orderService,
            IConfigService configService)
        {
            _cache = cache;
            _userRepo = userRepo;
            _orderService = orderService;
            _configService = configService;
        }

        /// <summary>
        /// Simplest possible usage - identical to FluentCache
        /// </summary>
        public async Task<User> GetUser_Simplest(int userId)
        {
            return await _cache.GetOrCreateAsync(
                () => _userRepo.GetUserAsync(userId)
            );
            // Key automatically generated from method + userId using FastHashKeyGenerator
        }

        /// <summary>
        /// With cache configuration - still very clean
        /// </summary>
        public async Task<User> GetUser_WithConfig(int userId)
        {
            return await _cache.GetOrCreateAsync(
                () => _userRepo.GetUserAsync(userId),
                opts => opts.WithDuration(TimeSpan.FromHours(1))
            );
        }

        /// <summary>
        /// Multiple parameters - automatically handled
        /// </summary>
        public async Task<List<Order>> GetOrders_MultipleParams(
            int customerId,
            OrderStatus status,
            DateTime from,
            DateTime to)
        {
            return await _cache.GetOrCreateAsync(
                () => _orderService.GetOrdersAsync(customerId, status, from, to),
                opts => opts
                    .WithDuration(TimeSpan.FromMinutes(30))
                    .WithTags("orders", $"customer:{customerId}")
            );
            // FastHashKeyGenerator automatically handles all 4 parameters
        }

        /// <summary>
        /// Complex object parameters - FastHashKeyGenerator handles serialization
        /// </summary>
        public async Task<AnalyticsReport> GetReport_ComplexObject(ReportCriteria criteria)
        {
            return await _cache.GetOrCreateAsync(
                () => _orderService.GenerateReportAsync(criteria),
                opts => opts
                    .WithDuration(TimeSpan.FromHours(2))
                    .WithTags("analytics", "reports")
            );
            // FastHashKeyGenerator uses MessagePack for complex object serialization
        }

        /// <summary>
        /// Synchronous methods work too
        /// </summary>
        public async Task<AppSettings> GetConfig_Synchronous(string section)
        {
            return await _cache.GetOrCreateAsync(
                () => _configService.GetSettings(section),
                opts => opts.WithDuration(TimeSpan.FromMinutes(15))
            );
        }

        /// <summary>
        /// Conditional caching based on parameters
        /// </summary>
        public async Task<DataResult> GetData_Conditional(string query, bool useExpensiveAlgorithm)
        {
            if (!useExpensiveAlgorithm)
            {
                // Skip caching for simple queries
                return _configService.GetDataSimple(query);
            }

            return await _cache.GetOrCreateAsync(
                () => _configService.GetDataExpensive(query),
                opts => opts
                    .WithDuration(TimeSpan.FromHours(1))
                    .WithTags("expensive-data")
            );
        }

        /// <summary>
        /// Different cache durations based on user type
        /// </summary>
        public async Task<WeatherData> GetWeather_UserTypeBased(string location, bool isPremiumUser)
        {
            var duration = isPremiumUser ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30);

            return await _cache.GetOrCreateAsync(
                () => _configService.GetWeatherData(location),
                opts => opts
                    .WithDuration(duration)
                    .WithTags("weather", $"location:{location}")
            );
        }

        /// <summary>
        /// Comparison: Before vs After
        /// </summary>
        public class BeforeAfterComparison
        {
            private readonly ICacheManager _cache;
            private readonly IUserRepository _userRepo;

            public BeforeAfterComparison(ICacheManager cache, IUserRepository userRepo)
            {
                _cache = cache;
                _userRepo = userRepo;
            }

            // BEFORE: Original MethodCache (Manual Keys)
            public async Task<User> GetUser_Before(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    key: $"user:{userId}",  // Manual key construction
                    factory: (ctx, ct) => _userRepo.GetUserAsync(userId),
                    configure: opts => opts.WithDuration(TimeSpan.FromHours(1))
                );
            }

            // AFTER: Simplified MethodCache (FluentCache-like)
            public async Task<User> GetUser_After(int userId)
            {
                return await _cache.GetOrCreateAsync(
                    () => _userRepo.GetUserAsync(userId),  // Factory only!
                    opts => opts.WithDuration(TimeSpan.FromHours(1))
                );
                // Key automatically generated using FastHashKeyGenerator
                // More sophisticated than FluentCache's simple string concatenation
            }

            // BEFORE: Complex multi-parameter scenario (Error-prone)
            public async Task<List<Order>> GetOrders_Before(
                int customerId, OrderStatus status, DateTime from, DateTime to, bool includeItems)
            {
                // Manual key construction - easy to mess up
                var key = $"orders:cust{customerId}:status{status}:from{from:yyyyMMdd}:to{to:yyyyMMdd}:items{includeItems}";

                return await _cache.GetOrCreateAsync(
                    key: key,
                    factory: (ctx, ct) => _userRepo.GetOrdersAsync(customerId, status, from, to, includeItems),
                    configure: opts => opts.WithDuration(TimeSpan.FromMinutes(30))
                );
            }

            // AFTER: Complex scenario (Automatic)
            public async Task<List<Order>> GetOrders_After(
                int customerId, OrderStatus status, DateTime from, DateTime to, bool includeItems)
            {
                return await _cache.GetOrCreateAsync(
                    () => _userRepo.GetOrdersAsync(customerId, status, from, to, includeItems),
                    opts => opts.WithDuration(TimeSpan.FromMinutes(30))
                );
                // FastHashKeyGenerator handles all parameter types:
                // - int: optimized serialization
                // - enum: type-safe serialization
                // - DateTime: binary representation
                // - bool: optimized representation
                // Result: Collision-resistant hash instead of error-prone string
            }
        }

        /// <summary>
        /// Advanced scenarios showing the power still available
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
            /// Nested method calls work automatically
            /// </summary>
            public async Task<ProcessedData> ProcessData_Nested(RawData data)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.ProcessData(data.Transform().Normalize()),
                    opts => opts.WithDuration(TimeSpan.FromHours(1))
                );
                // Closure analysis captures the final transformed data
            }

            /// <summary>
            /// Async enumerable results
            /// </summary>
            public async Task<List<StreamItem>> GetStreamData_Materialized(string query)
            {
                return await _cache.GetOrCreateAsync(
                    async () =>
                    {
                        var items = new List<StreamItem>();
                        await foreach (var item in _dataService.GetStreamAsync(query))
                        {
                            items.Add(item);
                        }
                        return items;
                    },
                    opts => opts.WithDuration(TimeSpan.FromMinutes(30))
                );
            }

            /// <summary>
            /// Computed keys from context
            /// </summary>
            public async Task<UserProfile> GetProfile_Computed(User user)
            {
                var contextKey = $"{user.TenantId}:{user.Role}";

                return await _cache.GetOrCreateAsync(
                    () => _dataService.GetProfileAsync(user, contextKey),
                    opts => opts
                        .WithDuration(TimeSpan.FromMinutes(60))
                        .WithTags("profiles", $"tenant:{user.TenantId}")
                );
            }
        }

        /// <summary>
        /// FluentCache vs MethodCache comparison
        /// </summary>
        public class FluentCacheComparison
        {
            // FluentCache approach (simple but limited key generation)
            public void FluentCache_Example()
            {
                // cache.Method(r => r.DoSomeWork(parameter)).GetValue();
                //
                // Pros:
                // - Very simple API
                // - Automatic key generation
                //
                // Cons:
                // - Basic string concatenation for keys
                // - No collision resistance
                // - Limited configuration options
                // - Performance overhead from expression analysis
            }

            // MethodCache with simplified API (sophisticated but approachable)
            public async Task<DataResult> MethodCache_Example(int parameter)
            {
                return await _cache.GetOrCreateAsync(
                    () => _dataService.DoSomeWork(parameter),
                    opts => opts
                        .WithDuration(TimeSpan.FromMinutes(30))
                        .WithTags("work-results")
                        .WithStampedeProtection()
                        .WithMetrics(_metrics)
                );

                // Pros:
                // - FluentCache-like simplicity
                // - FastHashKeyGenerator: collision-resistant hashing
                // - Full MethodCache feature set (tags, stampede protection, etc.)
                // - Type-specific parameter serialization
                // - High performance
                //
                // Key generated: "DoSomeWork_a1b2c3d4e5f6g7h8" (FNV hash)
                // vs FluentCache: "DataService.DoSomeWork:parameter:123" (string concat)
            }
        }

        private readonly IMetrics _metrics = null!; // For example purposes
    }

    // Supporting interfaces and types
    public interface IUserRepository
    {
        Task<User> GetUserAsync(int userId);
        Task<List<Order>> GetOrdersAsync(int customerId, OrderStatus status, DateTime from, DateTime to, bool includeItems = false);
    }

    public interface IOrderService
    {
        Task<List<Order>> GetOrdersAsync(int customerId, OrderStatus status, DateTime from, DateTime to);
        Task<AnalyticsReport> GenerateReportAsync(ReportCriteria criteria);
    }

    public interface IConfigService
    {
        AppSettings GetSettings(string section);
        DataResult GetDataSimple(string query);
        DataResult GetDataExpensive(string query);
        WeatherData GetWeatherData(string location);
    }

    public interface IDataService
    {
        Task<DataResult> DoSomeWork(int parameter);
        Task<ProcessedData> ProcessData(NormalizedData data);
        IAsyncEnumerable<StreamItem> GetStreamAsync(string query);
        Task<UserProfile> GetProfileAsync(User user, string contextKey);
    }

    public interface IMetrics { }

    public class User
    {
        public string TenantId { get; set; } = "";
        public string Role { get; set; } = "";
    }
    public class Order { }
    public class AppSettings { }
    public class AnalyticsReport { }
    public class ReportCriteria { }
    public class DataResult { }
    public class WeatherData { }
    public class RawData
    {
        public TransformedData Transform() => new();
    }
    public class TransformedData
    {
        public NormalizedData Normalize() => new();
    }
    public class NormalizedData { }
    public class ProcessedData { }
    public class StreamItem { }
    public class UserProfile { }

    public enum OrderStatus
    {
        Pending,
        Active,
        Completed
    }
}