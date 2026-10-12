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
    private int _reportCalls;
    private int _sessionCalls;
    private int _priceCalls;
    public int ReportCalls => Volatile.Read(ref _reportCalls);
    public int SessionCalls => Volatile.Read(ref _sessionCalls);
    public int PriceCalls => Volatile.Read(ref _priceCalls);
    public TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> GetReportAsync() => Task.FromResult(Interlocked.Increment(ref _reportCalls));
    public Task<int> GetSessionAsync(string id) => Task.FromResult(Interlocked.Increment(ref _sessionCalls));

    public async Task<int> GetPricesAsync()
    {
        var revision = Interlocked.Increment(ref _priceCalls);
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
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public decimal Rate { get; set; } = 100m;
    public bool Failing { get; set; }
    public TaskCompletionSource<decimal>? SlowResponse { get; set; }
    public TaskCompletionSource SlowCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<decimal> GetAsync(string pair)
    {
        Interlocked.Increment(ref _calls);
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
