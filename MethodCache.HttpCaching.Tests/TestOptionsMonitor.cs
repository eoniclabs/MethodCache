using Microsoft.Extensions.Options;

namespace MethodCache.HttpCaching.Tests;

public sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>
    where TOptions : class
{
    private TOptions _currentValue;

    public TestOptionsMonitor(TOptions currentValue)
    {
        _currentValue = currentValue;
    }

    public TOptions CurrentValue => _currentValue;

    public TOptions Get(string? name) => _currentValue;

    public IDisposable OnChange(Action<TOptions, string?> listener) => NullDisposable.Instance;

    public void Update(TOptions value) => _currentValue = value;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose()
        {
        }
    }
}
