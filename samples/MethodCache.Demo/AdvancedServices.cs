using System.Collections.Concurrent;

namespace MethodCache.Demo;

public interface ITimedReports
{
    [Cache(Duration = "00:00:10")]
    Task<int> GetReportAsync();

    [Cache(Duration = "00:00:30", SlidingExpiration = "00:00:10")]
    Task<int> GetSessionAsync(string id);

    [Cache(Duration = "00:00:10", RefreshAhead = "00:00:03")]
    Task<int> GetPricesAsync();
}

public sealed class TimedReports : ITimedReports
{
    public int ReportCalls { get; private set; }
    public int SessionCalls { get; private set; }
    public int PriceCalls { get; private set; }
    public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> GetReportAsync() => Task.FromResult(++ReportCalls);
    public Task<int> GetSessionAsync(string id) => Task.FromResult(++SessionCalls);

    public async Task<int> GetPricesAsync()
    {
        var revision = ++PriceCalls;
        if (revision == 2)
        {
            RefreshStarted.TrySetResult();
            await ReleaseRefresh.Task;
        }
        return revision;
    }
}

public interface IResilientRates
{
    [Cache(Duration = "00:00:10", FailSafe = "00:01:00", FailSafeTimeout = "00:00:02")]
    Task<decimal> GetAsync(string pair);
}

public sealed class ResilientRates : IResilientRates
{
    public int Calls { get; private set; }
    public decimal Rate { get; set; } = 100m;
    public bool Failing { get; set; }
    public TaskCompletionSource<decimal>? SlowResponse { get; set; }
    public TaskCompletionSource SlowCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<decimal> GetAsync(string pair)
    {
        Calls++;
        if (Failing) throw new HttpRequestException("The demo exchange-rate API is unavailable.");
        if (SlowResponse is { } response)
        {
            SlowCallStarted.TrySetResult();
            return response.Task;
        }
        return Task.FromResult(Rate);
    }
}

public interface IAvailableProducts
{
    [Cache(Duration = "00:05:00")]
    Task<string?> FindAsync(int id);
}

public sealed class AvailableProducts : IAvailableProducts
{
    public int Calls { get; private set; }
    public Dictionary<int, string> Products { get; } = new();
    public Task<string?> FindAsync(int id)
    {
        Calls++;
        return Task.FromResult(Products.GetValueOrDefault(id));
    }
}

public static class DemoTenants
{
    public static readonly Guid Alpha = new("00000000-0000-0000-0000-000000000001");
    public static readonly Guid Beta = new("00000000-0000-0000-0000-000000000002");
}

public sealed record TenantProduct(Guid TenantId, int Id, decimal Price);

public interface ITenantCatalog
{
    [Cache(Duration = "00:05:00", Tags = ["tenant:{tenantId}:product:{id}"])]
    Task<TenantProduct> GetAsync(Guid tenantId, int id);

    [CacheInvalidate(Tags = ["tenant:{tenantId}:product:{id}"])]
    Task UpdatePriceAsync(Guid tenantId, int id, decimal price);
}

public sealed class TenantCatalog : ITenantCatalog
{
    private readonly ConcurrentDictionary<(Guid Tenant, int Id), decimal> _prices = new();
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    public Task<TenantProduct> GetAsync(Guid tenantId, int id)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new TenantProduct(tenantId, id, _prices.GetOrAdd((tenantId, id), tenantId == DemoTenants.Alpha ? 100m : 200m)));
    }

    public Task UpdatePriceAsync(Guid tenantId, int id, decimal price)
    {
        _prices[(tenantId, id)] = price;
        return Task.CompletedTask;
    }
}
