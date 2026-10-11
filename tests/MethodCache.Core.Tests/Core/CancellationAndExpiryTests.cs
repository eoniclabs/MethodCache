using FluentAssertions;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Core;

public class CancellationAndExpiryTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    private InMemoryCacheManager CreateManager(Action<MethodCacheMemoryOptions>? configure = null)
    {
        var options = new MethodCacheMemoryOptions { EnableBackgroundCleanup = false };
        configure?.Invoke(options);
        return new InMemoryCacheManager(new NullCacheMetricsProvider(), MsOptions.Create(options), _time);
    }

    [Fact]
    public async Task Sliding_IsCappedByDuration()
    {
        var manager = CreateManager();
        var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromHours(1), SlidingExpiration = TimeSpan.FromMinutes(5) };
        var calls = 0;
        Task<int> Factory() => Task.FromResult(++calls);

        await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        for (var i = 0; i < 14; i++) // read every 4 minutes for 56 minutes
        {
            _time.Advance(TimeSpan.FromMinutes(4));
            await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        }

        calls.Should().Be(1, "reads keep the entry alive within its Duration");

        _time.Advance(TimeSpan.FromMinutes(4)); // 60 minutes after the load
        await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        calls.Should().Be(2, "an entry is never older than its Duration, however often it is read");
    }

    [Fact]
    public async Task Sliding_WithoutDuration_ExpiresOnlyWhenIdle()
    {
        var manager = CreateManager(o => o.DefaultExpiration = TimeSpan.FromMinutes(10));
        var policy = CacheRuntimePolicy.Empty("M") with { SlidingExpiration = TimeSpan.FromMinutes(5) };
        var calls = 0;
        Task<int> Factory() => Task.FromResult(++calls);

        await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        for (var i = 0; i < 10; i++) // 40 minutes, past the default expiration
        {
            _time.Advance(TimeSpan.FromMinutes(4));
            await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        }

        calls.Should().Be(1);

        _time.Advance(TimeSpan.FromMinutes(6));
        await manager.GetOrCreateFastAsync("key", "M", Factory, policy);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task FailSafe_WithSliding_RetriesTheFactoryAfterTheThrottle()
    {
        var manager = CreateManager();
        var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(10), SlidingExpiration = TimeSpan.FromMinutes(10), FailSafe = TimeSpan.FromHours(1) }; // throttle 30 s

        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v1"), policy);
        _time.Advance(TimeSpan.FromMinutes(11));
        (await manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TimeoutException(), policy)).Should().Be("v1");

        var retries = 0;
        Task<string> Healthy() { retries++; return Task.FromResult("v2"); }

        _time.Advance(TimeSpan.FromSeconds(20));
        (await manager.GetOrCreateFastAsync("key", "M", Healthy, policy)).Should().Be("v1", "the throttle holds the stale value");
        retries.Should().Be(0);

        _time.Advance(TimeSpan.FromSeconds(20));
        (await manager.GetOrCreateFastAsync("key", "M", Healthy, policy)).Should().Be("v2", "reads during the throttle must not extend the stale value");
        retries.Should().Be(1);
    }

    [Fact]
    public async Task FailSafe_CallerCancellation_IsNotADependencyFailure()
    {
        var manager = CreateManager();
        var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1), FailSafe = TimeSpan.FromHours(1) };
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v1"), policy);
        _time.Advance(TimeSpan.FromMinutes(2));

        using var cts = new CancellationTokenSource();
        var cancelled = manager.GetOrCreateFastAsync("key", "M", async () => { await Task.Delay(Timeout.Infinite, cts.Token); return "never"; }, policy, cts.Token);
        cts.Cancel();
        await FluentActions.Awaiting(() => cancelled).Should().ThrowAsync<OperationCanceledException>("the caller gave up, so it gets no stale value");

        var result = await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v2"), policy);
        result.Should().Be("v2", "a cancelled request does not throttle other callers onto the stale value");
    }

    [Fact]
    public async Task FailSafe_TimeoutThatIsNotTheCallers_ServesTheStaleValue()
    {
        // HttpClient reports its own timeout as TaskCanceledException, while the caller's token is not cancelled
        var manager = CreateManager();
        var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1), FailSafe = TimeSpan.FromHours(1) };
        await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("v1"), policy);
        _time.Advance(TimeSpan.FromMinutes(2));

        using var caller = new CancellationTokenSource();
        var result = await manager.GetOrCreateFastAsync<string>("key", "M", () => throw new TaskCanceledException("timed out"), policy, caller.Token);

        result.Should().Be("v1");
    }

    [Fact]
    public async Task CallerThatJoinedALoad_IsNotFailedWhenTheCallerThatStartedItCancels()
    {
        var manager = CreateManager();
        var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1) };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var calls = 0;

        async Task<string> Load(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            // A's load only ends through its cancellation, so a slow machine cannot let it finish first
            await Task.Delay(token.CanBeCanceled ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(100), token);
            return "value";
        }

        var a = manager.GetOrCreateFastAsync("key", "M", () => Load(cts.Token), policy, cts.Token);
        await started.Task;
        var b = manager.GetOrCreateFastAsync("key", "M", () => Load(CancellationToken.None), policy);
        cts.Cancel();

        await FluentActions.Awaiting(() => a).Should().ThrowAsync<OperationCanceledException>();
        (await b).Should().Be("value", "caller B never cancelled");
        calls.Should().Be(2, "B loads again after A's load was cancelled");
    }

    [Fact]
    public async Task CallersThatJoinedALoad_LoadAgainOnlyOnce_WhenTheCallerThatStartedItCancels()
    {
        // Found by ConcurrentLoadPropertyTests (shrunk: 4 callers, the first cancels, the factory succeeds; the
        // factory ran 3 times). The callers that joined retried one by one, and each one that came after the reload
        // had finished ran the factory again instead of using the value it had stored.
        for (var round = 0; round < 20; round++)
        {
            var manager = CreateManager();
            var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1) };
            using var cts = new CancellationTokenSource();
            var calls = 0;

            var a = manager.GetOrCreateFastAsync("key", "M", async () =>
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
                return 0;
            }, policy, cts.Token);
            var others = Enumerable.Range(0, 3)
                .Select(_ => manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(Interlocked.Increment(ref calls)), policy))
                .ToArray();
            cts.Cancel();

            await FluentActions.Awaiting(() => a).Should().ThrowAsync<OperationCanceledException>();
            (await Task.WhenAll(others)).Should().AllBeEquivalentTo(1, "the callers that waited share one reload");
            calls.Should().Be(1);
        }
    }

    [Fact]
    public async Task CallerThatJoinedALoad_CanStopWaitingWithoutCancellingIt()
    {
        var manager = CreateManager();
        var policy = CacheRuntimePolicy.Empty("M") with { Duration = TimeSpan.FromMinutes(1) };
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var a = manager.GetOrCreateFastAsync("key", "M", () => release.Task, policy);
        var b = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("b"), policy, cts.Token);
        cts.Cancel();

        await FluentActions.Awaiting(() => b).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult("a");
        (await a).Should().Be("a");
        (await manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult("c"), policy)).Should().Be("a");
    }

    [Fact]
    public async Task AlreadyCancelledCaller_GetsACancelledTaskOnAMiss()
    {
        var manager = CreateManager();
        var policy = CacheRuntimePolicy.Empty("M");
        var calls = 0;

        var task = manager.GetOrCreateFastAsync("key", "M", () => Task.FromResult(++calls), policy, new CancellationToken(canceled: true));

        task.IsCanceled.Should().BeTrue();
        calls.Should().Be(0);
    }

    [Fact]
    public async Task Fluent_OneCallerCancelling_DoesNotFailOtherCallers()
    {
        var manager = CreateManager();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var calls = 0;

        async ValueTask<string> Factory(CacheContext _, CancellationToken ct)
        {
            // The first load only ends through A's cancellation, so a slow machine cannot let it finish first
            var first = Interlocked.Increment(ref calls) == 1;
            started.TrySetResult();
            await Task.Delay(first ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(100), ct);
            return "value";
        }

        var a = manager.GetOrCreateAsync<string>("k", Factory, cancellationToken: cts.Token).AsTask();
        await started.Task;
        var b = manager.GetOrCreateAsync<string>("k", Factory).AsTask();
        cts.Cancel();

        await FluentActions.Awaiting(() => a).Should().ThrowAsync<OperationCanceledException>();
        (await b).Should().Be("value", "caller B never cancelled");
    }

    [Fact]
    public async Task FluentMany_WithVersion_HitsOnTheSecondCall()
    {
        var manager = CreateManager();
        var calls = 0;
        ValueTask<IDictionary<string, string>> Factory(IReadOnlyList<string> keys, CacheContext _, CancellationToken __)
        {
            calls++;
            return new ValueTask<IDictionary<string, string>>(keys.ToDictionary(k => k, k => "v" + k));
        }

        await manager.GetOrCreateManyAsync(new[] { "a", "b" }, Factory, b => b.WithVersion(1));
        var second = await manager.GetOrCreateManyAsync(new[] { "a", "b" }, Factory, b => b.WithVersion(1));

        calls.Should().Be(1);
        second.Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "va", ["b"] = "vb" });
    }

    [Fact]
    public async Task FluentTryGet_FindsAnEntryStoredWithItsVersion()
    {
        var manager = CreateManager();
        await manager.GetOrCreateAsync<string>("k", (_, _) => new ValueTask<string>("v"), b => b.WithVersion(2));

        (await manager.TryGetAsync<string>("k", version: 2)).Found.Should().BeTrue();
        (await manager.TryGetAsync<string>("k", version: 3)).Found.Should().BeFalse();
        (await manager.TryGetAsync<string>("k")).Found.Should().BeFalse();
    }

    [Fact]
    public async Task FluentHit_RunsHitCallbacks()
    {
        var manager = CreateManager();
        var hits = 0;
        await manager.GetOrCreateAsync<string>("k", (_, _) => new ValueTask<string>("v"));

        var value = await manager.GetOrCreateAsync<string>("k", (_, _) => new ValueTask<string>("other"), b => b.OnHit(_ => hits++));

        value.Should().Be("v");
        hits.Should().Be(1);
    }
}
