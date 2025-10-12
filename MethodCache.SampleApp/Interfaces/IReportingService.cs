using MethodCache.Core;
using MethodCache.SampleApp.Models;
using System.Threading.Tasks;
using MethodCache.Core;

namespace MethodCache.SampleApp.Interfaces
{
    /// <summary>
    /// Reporting service demonstrating long-running cache scenarios
    /// </summary>
    public interface IReportingService
    {
        // Daily reports - cached for longer periods
        [Cache("DailyReports")]
        Task<DailyReport> GenerateDailyReportAsync(DateTime date);

        // Weekly reports - expensive to generate
        [Cache("WeeklyReports")]
        Task<WeeklyReport> GenerateWeeklyReportAsync(DateTime weekStartDate);

        // Monthly reports - very expensive, cache for hours
        [Cache("MonthlyReports")]
        Task<MonthlyReport> GenerateMonthlyReportAsync(int year, int month);

        // Real-time dashboard data - short cache duration
        [Cache("Dashboard")]
        Task<DashboardData> GetDashboardDataAsync();

        // Executive summary - expensive aggregation
        [Cache("ExecutiveSummary")]
        Task<ExecutiveSummary> GenerateExecutiveSummaryAsync(DateTime fromDate, DateTime toDate);

        // Custom report generation - parameterized caching
        [Cache("CustomReports")]
        Task<CustomReport> GenerateCustomReportAsync(CustomReportCriteria criteria);

        // KPI calculations - computationally expensive
        [Cache("KPICalculations")]
        Task<KPIMetrics> CalculateKPIsAsync(KPICriteria criteria);

        // Data export preparation - large datasets
        [Cache("DataExports")]
        Task<ExportData> PrepareDataExportAsync(ExportCriteria criteria);

        // Cache invalidation when core data changes
        [CacheInvalidate(Tags = new[] { "DailyReports", "WeeklyReports", "MonthlyReports", "Dashboard" })]
        Task InvalidateReportsAsync();

        // Specific report invalidation
        [CacheInvalidate(Tags = new[] { "CustomReports", "KPICalculations" })]
        Task InvalidateCustomReportsAsync();

        // Export cleanup (no caching)
        Task CleanupExpiredExportsAsync();

        // Report scheduling (no caching)
        Task ScheduleReportAsync(ReportSchedule schedule);
    }
}