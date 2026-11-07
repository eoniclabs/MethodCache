using System.Diagnostics;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.PolicyPipeline.Model;
using MethodCache.Core.Tests.FaultInjection;
using MethodCache.Extensibility;
using MethodCache.Policies;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

/// <summary>
/// With a distributed cache and a backplane, a runtime override applies on every instance: it is stored in the
/// distributed cache, the others read it when the backplane tells them the method changed, and an instance that starts
/// reads the active ones.
/// </summary>
public sealed class SharedRuntimeOverridesTests : IDisposable
{
    private static readonly string GetId = MethodIds.For(typeof(ISharedCatalog), nameof(ISharedCatalog.GetAsync));
    private static readonly TimeSpan RealTimeLimit = TimeSpan.FromSeconds(10);

    public interface ISharedCatalog
    {
        [Cache(Duration = "10m")] Task<string> GetAsync(int id);
    }

    public sealed class Loads
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public int Next() => Interlocked.Increment(ref _count);
    }

    public sealed class SharedCatalog(Loads loads) : ISharedCatalog
    {
        public Task<string> GetAsync(int id) => Task.FromResult($"product-{id}-{loads.Next()}");
    }

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FailingStore _store;
    private readonly ChaosBackplaneHub _hub;
    private readonly Loads _loads = new();
    private readonly List<ServiceProvider> _providers = [];

    public SharedRuntimeOverridesTests()
    {
        _store = new FailingStore(new MethodCache.TestSupport.AtomicTestCache(_time));
        _hub = new ChaosBackplaneHub(_time);
    }

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
    }

    private sealed record Node(ServiceProvider Provider, ChaosBackplaneHub.Member? Backplane)
    {
        public IRuntimeCacheConfigurator Configurator => Provider.GetRequiredService<IRuntimeCacheConfigurator>();
        public ISharedCatalog Catalog => Provider.GetRequiredService<ISharedCatalog>();
        public TimeSpan? Duration => Provider.GetRequiredService<IPolicyRegistry>().GetPolicy(GetId).Policy.Duration;
    }

    /// <summary>Starts an instance; with <paramref name="host"/>, as a generic host starts it.</summary>
    private Node Start(bool backplane = true, bool host = true)
    {
        var member = backplane ? _hub.Join() : null;
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton(_loads);
        if (member != null) services.AddKeyedSingleton<IBackplane>(global::MethodCache.Infrastructure.OwnedServices.Key, member);
        services.AddSingleton<ISharedCatalog, SharedCatalog>();
        services.AddMethodCache();
        services.AddMethodCacheDistributedCache(_ => _store, options => options.ClockSkewTolerance = TimeSpan.Zero);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        // What a generic host runs in StartAsync
        if (host) provider.GetRequiredService<IStartupValidator>().Validate();
        return new Node(provider, member);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > RealTimeLimit) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(10);
        }
    }

    private static readonly RuntimeOverrideMetadata Incident = new() { Owner = "ops", Reason = "Incident 4211", Ticket = "INC-4211" };

    [Fact]
    public async Task AnOverride_AppliesOnEveryInstance_WithItsAuditMetadata()
    {
        var a = Start();
        var b = Start();
        Assert.Equal(TimeSpan.FromMinutes(10), b.Duration);

        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)), Incident);

        await Eventually(() => b.Duration == TimeSpan.FromMinutes(2));
        var applied = Assert.Single(b.Configurator.GetOverrides());
        Assert.Equal(GetId, applied.MethodId);
        Assert.Equal(TimeSpan.FromMinutes(2), applied.Options.Duration);
        Assert.Equal(Incident, applied.Metadata);
        Assert.Equal(Assert.Single(a.Configurator.GetOverrides()).UpdatedAt, applied.UpdatedAt);
    }

    [Fact]
    public async Task RemovingAnOverride_RemovesItOnEveryInstance()
    {
        var a = Start();
        var b = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        await Eventually(() => b.Duration == TimeSpan.FromMinutes(2));

        await b.Configurator.RemoveAsync(GetId);

        await Eventually(() => a.Duration == TimeSpan.FromMinutes(10));
        Assert.Empty(a.Configurator.GetOverrides());
        Assert.Empty(b.Configurator.GetOverrides());

        // Both share the entries of the method's own settings again
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(await a.Catalog.GetAsync(1), await b.Catalog.GetAsync(1));
        Assert.Equal(1, _loads.Count);
    }

    [Fact]
    public async Task ClearingTheOverrides_ClearsThemOnEveryInstance()
    {
        var a = Start();
        var b = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        await Eventually(() => b.Duration == TimeSpan.FromMinutes(2));

        await a.Configurator.ClearAsync();

        await Eventually(() => b.Duration == TimeSpan.FromMinutes(10));
        Assert.Empty(b.Configurator.GetOverrides());
    }

    [Fact]
    public async Task AnInstanceThatStarts_ReadsTheActiveOverrides_BeforeItsFirstCall()
    {
        var a = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)), Incident);
        a.Provider.Dispose();

        var restarted = Start();

        Assert.Equal(TimeSpan.FromMinutes(2), restarted.Duration);
        Assert.Equal(Incident, Assert.Single(restarted.Configurator.GetOverrides()).Metadata);
    }

    [Fact]
    public async Task AnInstanceWithoutAHost_ReadsTheActiveOverrides_WithoutHoldingUpItsFirstCall()
    {
        var a = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));

        var started = Start(host: false);
        _ = await started.Catalog.GetAsync(1);

        await Eventually(() => started.Duration == TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task InstancesWithTheSameOverride_ShareItsEntries()
    {
        var a = Start();
        var b = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        await Eventually(() => b.Duration == TimeSpan.FromMinutes(2));
        _time.Advance(TimeSpan.FromMilliseconds(1));

        var first = await a.Catalog.GetAsync(1);
        var second = await b.Catalog.GetAsync(1);

        Assert.Equal(first, second);
        Assert.Equal(1, _loads.Count);
    }

    [Fact]
    public async Task AnOverrideThatEnds_EndsOnEveryInstance()
    {
        var a = Start();
        var b = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)),
            new RuntimeOverrideMetadata { ExpiresAt = _time.GetUtcNow().AddHours(1) });
        await Eventually(() => b.Duration == TimeSpan.FromMinutes(2));

        _time.Advance(TimeSpan.FromHours(1));

        await Eventually(() => a.Duration == TimeSpan.FromMinutes(10) && b.Duration == TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), Start().Duration);
    }

    [Fact]
    public async Task AFunction_CannotBePartOfAnOverrideThatEveryInstanceApplies()
    {
        var a = Start();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            a.Configurator.UpsertAsync(GetId, o => o.DurationFor<string>(_ => TimeSpan.FromMinutes(1))).AsTask());

        Assert.Contains("DurationFor", error.Message, StringComparison.Ordinal);
        Assert.Empty(a.Configurator.GetOverrides());
    }

    [Fact]
    public async Task WithoutABackplane_AnOverrideStaysOnItsInstance_AndMayHoldAFunction()
    {
        var a = Start(backplane: false);
        var b = Start(backplane: false);

        await a.Configurator.UpsertAsync(GetId, o => o.DurationFor<string>(_ => TimeSpan.FromMinutes(1)));

        Assert.Single(a.Configurator.GetOverrides());
        Assert.Empty(b.Configurator.GetOverrides());
        Assert.Empty(Start(backplane: false).Configurator.GetOverrides());
    }

    [Fact]
    public async Task WhenTheDistributedCacheDoesNotAnswer_AnUpsertThrows_AndChangesNothing()
    {
        var a = Start();
        var b = Start();
        Assert.Equal(TimeSpan.FromMinutes(10), a.Duration);
        _store.FailOverrides = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2))).AsTask());

        Assert.Empty(a.Configurator.GetOverrides());
        Assert.Equal(TimeSpan.FromMinutes(10), a.Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), b.Duration);
    }

    [Fact]
    public async Task AnInstanceThatStartsWhileTheCacheDoesNotAnswer_StartsWithout_AndReadsThemLater()
    {
        var a = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        _store.FailOverrides = true;

        var started = Start();
        Assert.Equal(TimeSpan.FromMinutes(10), started.Duration);

        _store.FailOverrides = false;
        _time.Advance(new DistributedCacheOptions().CircuitBreakerDuration);

        await Eventually(() => started.Duration == TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task AnInstanceThatMissedAMessage_ReadsTheOverridesAgain_WhenItsBackplaneSaysItMayHaveMissedSome()
    {
        var a = Start();
        var b = Start();
        Assert.Equal(TimeSpan.FromMinutes(10), b.Duration);
        b.Backplane!.Disconnected = true;

        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        Assert.Equal(TimeSpan.FromMinutes(10), b.Duration);

        // What every backplane delivers after it reconnects: the instance may have missed invalidations
        b.Backplane.Disconnected = false;
        await b.Backplane.Handler!(new BackplaneMessage { Type = BackplaneMessageType.ClearAll });

        await Eventually(() => b.Duration == TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task AnOverride_IsAnnouncedOnce_HoweverManyInstancesApplyIt()
    {
        var a = Start();
        var b = Start();
        var c = Start();
        // Each instance has served the method, so applying the override changes how each caches it
        foreach (var node in new[] { a, b, c }) _ = await node.Catalog.GetAsync(1);
        var published = _hub.Published;

        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        await Eventually(() => b.Duration == TimeSpan.FromMinutes(2) && c.Duration == TimeSpan.FromMinutes(2));
        await Task.Delay(200);

        // a's invalidation told b and c; they drop their own entries without telling anyone again
        Assert.Equal(1, _hub.Published - published);
    }

    [Fact]
    public async Task AChangeToTheAuditFieldsOnly_ReachesEveryInstance()
    {
        var a = Start();
        var b = Start();
        _ = await a.Catalog.GetAsync(1);
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)), Incident);
        await Eventually(() => b.Configurator.GetOverrides().SingleOrDefault()?.Metadata == Incident);

        var resolved = Incident with { Reason = "Incident 4211 resolved" };
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)), resolved);

        await Eventually(() => b.Configurator.GetOverrides().SingleOrDefault()?.Metadata == resolved);
    }

    [Fact]
    public async Task AnOverrideThatKeepsTheMethodsSettings_ReachesEveryInstance()
    {
        var a = Start();
        var b = Start();

        // The attribute's own duration, so nothing changes how either instance caches
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(10)), Incident);

        await Eventually(() => b.Configurator.GetOverrides().SingleOrDefault()?.Metadata == Incident);
    }

    [Fact]
    public async Task ClearingOnAnInstanceThatServedNothing_ClearsTheOverridesOnTheOthers()
    {
        var a = Start();
        _ = await a.Catalog.GetAsync(1);
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        var b = Start();

        await b.Configurator.ClearAsync();

        await Eventually(() => a.Duration == TimeSpan.FromMinutes(10));
        Assert.Empty(a.Configurator.GetOverrides());
    }

    [Fact]
    public async Task Clearing_RemovesTheStoredOverrides_ThisInstanceHasNotReadYet()
    {
        var a = Start();
        var b = Start();
        _hub.Mode = BackplaneChaos.Hold;
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        Assert.Empty(b.Configurator.GetOverrides());

        await b.Configurator.ClearAsync();
        _hub.Mode = BackplaneChaos.Deliver;
        await _hub.DeliverHeld();

        await Eventually(() => a.Duration == TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromMinutes(10), b.Duration);
        Assert.Equal(TimeSpan.FromMinutes(10), Start().Duration);
    }

    [Fact]
    public async Task ClearingWhileTheCacheDoesNotAnswer_Throws()
    {
        var a = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        _store.FailOverrides = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => a.Configurator.ClearAsync().AsTask());

        Assert.Single(a.Configurator.GetOverrides());
    }

    [Fact]
    public async Task AHostThatStarts_WaitsForTheOverrides_AlsoWhenAServiceResolvedEarlierStartedReadingThem()
    {
        var a = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        _store.HoldOverrideReads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A hosted service's dependency resolves and is called before the host validates the cache
        var started = Start(host: false);
        _ = await started.Catalog.GetAsync(1);
        var validated = Task.Run(() => started.Provider.GetRequiredService<IStartupValidator>().Validate());
        await Task.Delay(200);
        Assert.False(validated.IsCompleted);

        _store.HoldOverrideReads.SetResult();
        await validated.WaitAsync(RealTimeLimit);

        Assert.Equal(TimeSpan.FromMinutes(2), started.Duration);
    }

    [Fact]
    public void TheStoredRecord_KeepsEverySettingAndTheAuditMetadata()
    {
        var policy = CachePolicy.Empty with
        {
            Duration = TimeSpan.FromMinutes(2),
            SlidingExpiration = TimeSpan.FromSeconds(30),
            RefreshAhead = TimeSpan.FromSeconds(10),
            FailSafe = TimeSpan.FromHours(1),
            FailSafeTimeout = TimeSpan.FromMilliseconds(250),
            FailSafeRetryAfter = TimeSpan.FromSeconds(5),
            Version = 3,
            Enabled = true,
            Distributed = false,
            Tags = ["products", "products:{id}"],
        };
        var fields = CachePolicyFields.Duration | CachePolicyFields.SlidingExpiration | CachePolicyFields.RefreshAhead |
                     CachePolicyFields.FailSafe | CachePolicyFields.FailSafeTimeout | CachePolicyFields.FailSafeRetryAfter |
                     CachePolicyFields.Version | CachePolicyFields.Enabled | CachePolicyFields.Distributed |
                     CachePolicyFields.Tags | CachePolicyFields.ClearTags;
        var metadata = Incident with { ExpiresAt = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.FromHours(1)) };
        var updatedAt = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

        var record = SharedRuntimeOverrides.Encode("r1", updatedAt, policy, fields | CachePolicyFields.Metadata, metadata);

        Assert.True(SharedRuntimeOverrides.TryDecode(record, out var stored));
        Assert.Equal("r1", stored.Revision);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        Assert.Equal(fields, stored.Fields);
        Assert.Equal(metadata, stored.Metadata);
        Assert.Equal(policy.Duration, stored.Policy.Duration);
        Assert.Equal(policy.SlidingExpiration, stored.Policy.SlidingExpiration);
        Assert.Equal(policy.RefreshAhead, stored.Policy.RefreshAhead);
        Assert.Equal(policy.FailSafe, stored.Policy.FailSafe);
        Assert.Equal(policy.FailSafeTimeout, stored.Policy.FailSafeTimeout);
        Assert.Equal(policy.FailSafeRetryAfter, stored.Policy.FailSafeRetryAfter);
        Assert.Equal(policy.Version, stored.Policy.Version);
        Assert.Equal(policy.Enabled, stored.Policy.Enabled);
        Assert.Equal(policy.Distributed, stored.Policy.Distributed);
        Assert.Equal(policy.Tags, stored.Policy.Tags);
    }

    [Theory]
    [InlineData("{\"v\":2,\"revision\":\"r\",\"updatedAt\":\"2026-11-07T12:00:00Z\",\"fields\":1}")]
    [InlineData("{\"v\":1}")]
    [InlineData("not json")]
    public void ARecordOfAnotherFormat_IsNotRead(string record)
        => Assert.False(SharedRuntimeOverrides.TryDecode(System.Text.Encoding.UTF8.GetBytes(record), out _));

    [Fact]
    public async Task ARecordOfAnotherFormat_DoesNotApply()
    {
        var a = Start();
        await a.Configurator.UpsertAsync(GetId, o => o.WithDuration(TimeSpan.FromMinutes(2)));
        a.Provider.Dispose();
        await _store.SetOverrideAsync(GetId, "{\"v\":2}"u8.ToArray());

        Assert.Equal(TimeSpan.FromMinutes(10), Start().Duration);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("tag")]
    public async Task ARecordThatDoesNotFitThisInstance_IsSkipped(string problem)
    {
        // Written by an instance with other limits or another version of the method, say
        var policy = problem == "duration"
            ? CachePolicy.Empty with { Duration = TimeSpan.FromDays(30) }
            : CachePolicy.Empty with { Tags = ["product:{missing}"] };
        var fields = problem == "duration" ? CachePolicyFields.Duration : CachePolicyFields.Tags;
        await _store.SetOverrideAsync(GetId, SharedRuntimeOverrides.Encode("r1", _time.GetUtcNow(), policy, fields, null));

        var started = Start();

        Assert.Equal(TimeSpan.FromMinutes(10), started.Duration);
        Assert.Empty(started.Configurator.GetOverrides());
    }

    /// <summary>A shared store whose override records can stop answering.</summary>
    private sealed class FailingStore(MethodCache.TestSupport.AtomicTestCache inner) : IAtomicInvalidationStore
    {
        public volatile bool FailOverrides;
        // While set, a read of an override record waits for it
        public volatile TaskCompletionSource? HoldOverrideReads;

        private void Check(string key)
        {
            if (FailOverrides && key.Contains("override:v1:", StringComparison.Ordinal)) throw new InvalidOperationException("The distributed cache doesn't answer.");
        }

        public Task SetOverrideAsync(string methodId, byte[] value)
            => inner.SetAsync("methodcache:override:v1:" + methodId, value, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1) });

        public byte[]? Get(string key) { Check(key); return inner.Get(key); }
        public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            Check(key);
            if (HoldOverrideReads is { } hold && key.Contains("override:v1:", StringComparison.Ordinal)) await hold.Task.WaitAsync(token);
            return await inner.GetAsync(key, token);
        }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { Check(key); inner.Set(key, value, options); }
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) { Check(key); return inner.SetAsync(key, value, options, token); }
        public void Refresh(string key) => inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => inner.RefreshAsync(key, token);
        public void Remove(string key) { Check(key); inner.Remove(key); }
        public Task RemoveAsync(string key, CancellationToken token = default) { Check(key); return inner.RemoveAsync(key, token); }
        public Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
            => inner.WriteInvalidationStampAsync(key, timestamp, retention, cancellationToken);
    }
}
