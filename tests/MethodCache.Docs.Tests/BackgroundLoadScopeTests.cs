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

public class BackgroundLoadSeveralServicesTests
{
    // A manual refresh-ahead whose factory calls several scoped cached services: every scope the refresh creates is
    // disposed when it ends, and a call the factory leaves running after it returned gets no scope at all
    [Theory]
    [InlineData("sequential")]
    [InlineData("when-all")]
    [InlineData("late")]
    public async Task ManualRefresh_CallingSeveralScopedServices_DisposesEveryScopeItCreates(string shape)
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<ScopeLog>();
        services.AddScoped<IScopedLeft, ScopedLeft>();
        services.AddScoped<IScopedRight, ScopedRight>();
        services.AddMethodCache();
        await using var provider = services.BuildServiceProvider();
        var log = provider.GetRequiredService<ScopeLog>();
        var cache = provider.GetRequiredService<ICacheManager>();
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lateCall = null;

        ValueTask<int> Call(IServiceProvider sp)
        {
            // Resolved by the request, as a factory capturing the request's services would be
            var left = sp.GetRequiredService<IScopedLeft>();
            var right = sp.GetRequiredService<IScopedRight>();
            return cache.GetOrCreateAsync(
                "several-scoped",
                async _ =>
                {
                    switch (shape)
                    {
                        case "sequential":
                            return await left.GetAsync(1) + await right.GetAsync(1);
                        case "when-all":
                            var both = await Task.WhenAll(left.GetAsync(1), right.GetAsync(1));
                            return both[0] + both[1];
                        default:
                            lateCall = Task.Run(async () =>
                            {
                                await late.Task;
                                await right.GetAsync(2);
                            });
                            return await left.GetAsync(1);
                    }
                },
                o => o.WithDuration(TimeSpan.FromSeconds(60)).WithRefreshAhead(TimeSpan.FromSeconds(30)));
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            await Call(scope.ServiceProvider);
        }

        // The cached services' own entries have expired; the manual entry is in its refresh window
        log.Hold();
        time.Advance(TimeSpan.FromSeconds(40));
        await using (var scope = provider.CreateAsyncScope())
        {
            await Call(scope.ServiceProvider);
        }

        var loadsBefore = log.Finished;
        log.Release();
        var refreshLoads = shape == "late" ? 1 : 2;
        Assert.True(SpinWait.SpinUntil(() => log.Finished >= loadsBefore + refreshLoads, TimeSpan.FromSeconds(10)), "The refresh did not run.");
        Assert.True(SpinWait.SpinUntil(() => log.Instances.All(i => i.Disposed), TimeSpan.FromSeconds(10)), $"{log.Instances.Count(i => !i.Disposed)} scope(s) of the refresh were not disposed.");

        if (lateCall != null)
        {
            late.SetResult();
            await lateCall.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.True(log.Instances.All(i => i.Disposed), $"{log.Instances.Count(i => !i.Disposed)} scope(s) created after the refresh ended were not disposed.");
    }
}

public interface IScopedLeft
{
    [Cache(Duration = "00:00:10")]
    Task<int> GetAsync(int id);
}

public interface IScopedRight
{
    [Cache(Duration = "00:00:10")]
    Task<int> GetAsync(int id);
}

public sealed class ScopeLog
{
    private TaskCompletionSource _gate = CompletedGate();
    private int _finished;

    public System.Collections.Concurrent.ConcurrentQueue<ScopedPart> Instances { get; } = new();

    public int Finished => Volatile.Read(ref _finished);

    public Task Gate => _gate.Task;

    public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    public void Done() => Interlocked.Increment(ref _finished);

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }
}

public abstract class ScopedPart : IDisposable
{
    private readonly ScopeLog _log;

    protected ScopedPart(ScopeLog log)
    {
        _log = log;
        log.Instances.Enqueue(this);
    }

    public bool Disposed { get; private set; }

    public async Task<int> GetAsync(int id)
    {
        await _log.Gate.ConfigureAwait(false);
        _log.Done();
        return id;
    }

    public void Dispose() => Disposed = true;
}

public sealed class ScopedLeft(ScopeLog log) : ScopedPart(log), IScopedLeft;

public sealed class ScopedRight(ScopeLog log) : ScopedPart(log), IScopedRight;

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
