using MethodCache.Demo;
using Xunit;

namespace MethodCache.Docs.Tests;

public class DemoCounterTests
{
    private const int Workers = 16;
    private const int CallsPerWorker = 4_000;

    [Fact]
    public async Task TimedReports_ConcurrentCalls_CountEverySourceCall()
    {
        var reports = new TimedReports();
        reports.ReleaseRefresh.SetResult();

        await RunConcurrentlyAsync(() =>
        {
            _ = reports.GetReportAsync();
            _ = reports.GetSessionAsync("session");
            _ = reports.GetPricesAsync();
        });

        Assert.Equal(Workers * CallsPerWorker, reports.ReportCalls);
        Assert.Equal(Workers * CallsPerWorker, reports.SessionCalls);
        Assert.Equal(Workers * CallsPerWorker, reports.PriceCalls);
    }

    [Fact]
    public async Task ResilientRates_ConcurrentCalls_CountEverySourceCall()
    {
        var rates = new ResilientRates();

        await RunConcurrentlyAsync(() => _ = rates.GetAsync("EUR/SEK"));

        Assert.Equal(Workers * CallsPerWorker, rates.Calls);
    }

    private static async Task RunConcurrentlyAsync(Action call)
    {
        using var start = new Barrier(Workers);
        var workers = Enumerable.Range(0, Workers).Select(_ => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < CallsPerWorker; i++) call();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        await Task.WhenAll(workers);
    }
}
