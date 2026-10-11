namespace Vendor.Rates;

public interface IRatesClient
{
    Task<decimal> GetRateAsync(string from, string to);

    Task<int> PlaceOrderAsync(string item);
}

public sealed class RatesClient : IRatesClient
{
    private static int _rateCalls;
    private static int _orderCalls;

    public static int RateCalls => _rateCalls;

    public static int OrderCalls => _orderCalls;

    public Task<decimal> GetRateAsync(string from, string to)
    {
        Interlocked.Increment(ref _rateCalls);
        return Task.FromResult(11.5m);
    }

    public Task<int> PlaceOrderAsync(string item) => Task.FromResult(Interlocked.Increment(ref _orderCalls));
}
