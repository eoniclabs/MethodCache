using MethodCache.Core;

namespace MethodCache.SampleApp.Models
{
    public class DailyReport
    {
        public DateTime Date { get; set; }
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public int NewUsers { get; set; }
        public int ActiveUsers { get; set; }
        public Dictionary<int, int> OrdersByHour { get; set; } = new();
        public List<TopProduct> TopProducts { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public class WeeklyReport
    {
        public DateTime WeekStartDate { get; set; }
        public DateTime WeekEndDate { get; set; }
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public decimal GrowthPercentage { get; set; }
        public Dictionary<DateTime, decimal> DailyRevenue { get; set; } = new();
        public List<TopProduct> TopProducts { get; set; } = new();
        public List<TopUser> TopUsers { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public class MonthlyReport
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public decimal MonthOverMonthGrowth { get; set; }
        public Dictionary<int, decimal> WeeklyRevenue { get; set; } = new();
        public Dictionary<int, int> ProductCategoryBreakdown { get; set; } = new();
        public CustomerSegmentAnalysis CustomerAnalysis { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public class DashboardData
    {
        public decimal TodaysRevenue { get; set; }
        public int TodaysOrders { get; set; }
        public int ActiveUsers { get; set; }
        public decimal AverageOrderValue { get; set; }
        public List<RecentOrder> RecentOrders { get; set; } = new();
        public List<TopProduct> TopProducts { get; set; } = new();
        public Dictionary<string, decimal> RevenueByHour { get; set; } = new();
        public DateTime LastUpdated { get; set; }
    }

    public class ExecutiveSummary
    {
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public decimal TotalRevenue { get; set; }
        public int TotalOrders { get; set; }
        public int TotalCustomers { get; set; }
        public decimal RevenueGrowth { get; set; }
        public decimal CustomerGrowth { get; set; }
        public List<KeyMetric> KeyMetrics { get; set; } = new();
        public MarketAnalysis MarketAnalysis { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public class CustomReport
    {
        public string ReportName { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public List<ChartData> Charts { get; set; } = new();
        public DateTime GeneratedAt { get; set; }
    }

    public class CustomReportCriteria : ICacheKeyProvider
    {
        public string ReportType { get; set; } = string.Empty;
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public List<string> Metrics { get; set; } = new();
        public List<string> Dimensions { get; set; } = new();
        public Dictionary<string, object> Filters { get; set; } = new();

        public string CacheKeyPart
        {
            get
            {
                var metricsKey = string.Join(",", Metrics.OrderBy(x => x));
                var dimensionsKey = string.Join(",", Dimensions.OrderBy(x => x));
                var filtersKey = string.Join(",", Filters.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
                return $"type:{ReportType}_from:{FromDate:yyyy-MM-dd}_to:{ToDate:yyyy-MM-dd}_metrics:{metricsKey}_dims:{dimensionsKey}_filters:{filtersKey}";
            }
        }
    }

    public class KPIMetrics
    {
        public Dictionary<string, decimal> Metrics { get; set; } = new();
        public DateTime CalculatedAt { get; set; }
    }

    public class KPICriteria : ICacheKeyProvider
    {
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public List<string> MetricNames { get; set; } = new();

        public string CacheKeyPart =>
            $"from:{FromDate:yyyy-MM-dd}_to:{ToDate:yyyy-MM-dd}_metrics:{string.Join(",", MetricNames.OrderBy(x => x))}";
    }

    public class ExportData
    {
        public string ExportId { get; set; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = string.Empty;
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public string ContentType { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public class ExportCriteria : ICacheKeyProvider
    {
        public string ExportType { get; set; } = string.Empty;
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public string Format { get; set; } = "CSV";
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string CacheKeyPart
        {
            get
            {
                var parametersKey = string.Join(",", Parameters.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
                return $"type:{ExportType}_from:{FromDate:yyyy-MM-dd}_to:{ToDate:yyyy-MM-dd}_format:{Format}_params:{parametersKey}";
            }
        }
    }

    public class ReportSchedule
    {
        public string ReportType { get; set; } = string.Empty;
        public string CronExpression { get; set; } = string.Empty;
        public List<string> Recipients { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
    }

    // Supporting classes
    public class TopProduct
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public decimal Revenue { get; set; }
    }

    public class TopUser
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public int Orders { get; set; }
        public decimal TotalSpent { get; set; }
    }

    public class RecentOrder
    {
        public int OrderId { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class CustomerSegmentAnalysis
    {
        public Dictionary<string, int> SegmentCounts { get; set; } = new();
        public Dictionary<string, decimal> SegmentRevenue { get; set; } = new();
    }

    public class KeyMetric
    {
        public string Name { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public string Unit { get; set; } = string.Empty;
        public decimal? ChangePercentage { get; set; }
    }

    public class MarketAnalysis
    {
        public decimal MarketSharePercentage { get; set; }
        public List<CompetitorData> Competitors { get; set; } = new();
    }

    public class CompetitorData
    {
        public string Name { get; set; } = string.Empty;
        public decimal MarketSharePercentage { get; set; }
    }

    public class ChartData
    {
        public string ChartType { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
    }
}