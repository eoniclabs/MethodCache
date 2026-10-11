namespace MethodCache.SourceGenerator.IntegrationTests.Vendor;

/// <summary>
/// Stands in for a client from a NuGet package: compiled test sources reference this assembly, so to the generator
/// this interface comes from metadata, as a vendor's would.
/// </summary>
public interface IFxVendorClient
{
    Task<decimal> GetRateAsync(string from, string to);

    ValueTask<string[]> GetCurrenciesAsync();

    int Square(int value);

    Task<int> CreateOrderAsync(string item);

    Task<decimal> GetOverloadedAsync(string from);

    Task<decimal> GetOverloadedAsync(string from, string to);

    Task PingAsync();
}

public sealed class FxVendorClient : IFxVendorClient
{
    private int _rateCalls;
    private int _currencyCalls;
    private int _squareCalls;
    private int _orderCalls;

    public int RateCalls => _rateCalls;

    public int CurrencyCalls => _currencyCalls;

    public int SquareCalls => _squareCalls;

    public int OrderCalls => _orderCalls;

    public Task<decimal> GetRateAsync(string from, string to)
    {
        Interlocked.Increment(ref _rateCalls);
        return Task.FromResult(from == to ? 1m : 11.5m);
    }

    public ValueTask<string[]> GetCurrenciesAsync()
    {
        Interlocked.Increment(ref _currencyCalls);
        return new ValueTask<string[]>(new[] { "EUR", "SEK" });
    }

    public int Square(int value)
    {
        Interlocked.Increment(ref _squareCalls);
        return value * value;
    }

    public Task<int> CreateOrderAsync(string item) => Task.FromResult(Interlocked.Increment(ref _orderCalls));

    public Task<decimal> GetOverloadedAsync(string from) => Task.FromResult(1m);

    public Task<decimal> GetOverloadedAsync(string from, string to) => Task.FromResult(1m);

    public Task PingAsync() => Task.CompletedTask;
}
