using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Docs.Tests;

/// <summary>
/// docs/features/expiration.md and fail-safe.md: a refresh-ahead, or a fail-safe call that goes on after its caller
/// got the expired value, runs a scoped service on a new instance from its own scope, disposed when it ends.
/// </summary>
public class BackgroundLoadScopeTests
{
    [Theory]
    [InlineData(nameof(IScopedQuotes.GetRefreshedAsync), 40)]
    [InlineData(nameof(IScopedQuotes.GetFailSafeAsync), 61)]
    public async Task BackgroundLoad_OfAScopedService_UsesANewInstance_DisposedWhenItEnds(string method, int advanceSeconds)
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<QuoteLog>();
        services.AddScoped<IScopedQuotes, ScopedQuotes>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var log = provider.GetRequiredService<QuoteLog>();
        Task<int> Call(IServiceProvider sp) => method == nameof(IScopedQuotes.GetRefreshedAsync)
            ? sp.GetRequiredService<IScopedQuotes>().GetRefreshedAsync(1)
            : sp.GetRequiredService<IScopedQuotes>().GetFailSafeAsync(1);

        log.Release(1);
        await using (var scope = provider.CreateAsyncScope())
        {
            Assert.Equal(1, await Call(scope.ServiceProvider));
        }

        // The second request gets the cached (or expired) value and ends while the load is still waiting
        log.Hold();
        time.Advance(TimeSpan.FromSeconds(advanceSeconds));
        await using (var scope = provider.CreateAsyncScope())
        {
            var call = Call(scope.ServiceProvider);
            if (!call.IsCompleted)
            {
                time.Advance(TimeSpan.FromSeconds(1)); // past the fail-safe timeout
            }

            Assert.Equal(1, await call);
        }

        log.Release(2);
        var loader = await log.Loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Request 1's instance, request 2's instance, then the load's own
        Assert.Equal(3, log.Created.Count);
        var requestInstance = log.Created[1];

        Assert.False(loader.DisposedBeforeItFinished);
        Assert.NotSame(requestInstance, loader);
        Assert.True(SpinWait.SpinUntil(() => loader.Disposed, TimeSpan.FromSeconds(10)));
    }
}

public interface IScopedQuotes
{
    [Cache(Duration = "00:01:00", RefreshAhead = "00:00:30")]
    Task<int> GetRefreshedAsync(int id);

    [Cache(Duration = "00:01:00", FailSafe = "01:00:00", FailSafeTimeout = "00:00:00.5")]
    Task<int> GetFailSafeAsync(int id);
}

public sealed class QuoteLog
{
    private TaskCompletionSource<int> _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<ScopedQuotes> Created { get; } = new();

    public TaskCompletionSource<ScopedQuotes> Loaded { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<int> Gate => _gate.Task;

    public void Hold()
    {
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release(int value) => _gate.TrySetResult(value);
}

public sealed class ScopedQuotes : IScopedQuotes, IDisposable
{
    private readonly QuoteLog _log;

    public ScopedQuotes(QuoteLog log)
    {
        _log = log;
        lock (log.Created)
        {
            log.Created.Add(this);
        }
    }

    public bool Disposed { get; private set; }

    public bool DisposedBeforeItFinished { get; private set; }

    public Task<int> GetRefreshedAsync(int id) => LoadAsync();

    public Task<int> GetFailSafeAsync(int id) => LoadAsync();

    private async Task<int> LoadAsync()
    {
        var loaded = _log.Loaded;
        var value = await _log.Gate.ConfigureAwait(false);
        DisposedBeforeItFinished = Disposed;
        loaded.TrySetResult(this);
        return value;
    }

    public void Dispose() => Disposed = true;
}
