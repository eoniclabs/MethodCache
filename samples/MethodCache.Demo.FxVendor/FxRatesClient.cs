namespace FxVendor;

/// <summary>An exchange-rate API client, as a vendor would ship it.</summary>
public interface IFxRatesClient
{
    Task<decimal> GetRateAsync(string from, string to);

    Task<string> PlaceOrderAsync(string from, string to, decimal amount);
}

public sealed class FxRatesClient : IFxRatesClient
{
    private int _rateCalls;
    private int _orderCalls;

    /// <summary>How many times the (slow, paid) rate API was called.</summary>
    public int RateCalls => Volatile.Read(ref _rateCalls);

    /// <summary>How many orders were placed.</summary>
    public int OrderCalls => Volatile.Read(ref _orderCalls);

    public async Task<decimal> GetRateAsync(string from, string to)
    {
        Interlocked.Increment(ref _rateCalls);
        await Task.Delay(250);
        return (from, to) switch
        {
            ("EUR", "SEK") => 11.42m,
            ("USD", "SEK") => 10.61m,
            _ => 1m,
        };
    }

    public Task<string> PlaceOrderAsync(string from, string to, decimal amount)
        => Task.FromResult($"order-{Interlocked.Increment(ref _orderCalls)}");
}
