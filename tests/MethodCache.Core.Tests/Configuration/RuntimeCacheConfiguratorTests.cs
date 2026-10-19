using System.Threading;
using System.Threading.Tasks;
using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class RuntimeCacheConfiguratorTests
{
    private interface IRuntimeDocService
    {
        Task<string> GetAsync(int id);
        Task<string> SearchAsync(string query);
    }

    [Fact]
    public async Task UpsertAsync_WithPolicyBuilder_StoresPolicy()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Sample.Method", builder =>
        {
            builder.WithDuration(System.TimeSpan.FromMinutes(2));
            builder.WithTags("runtime");
            builder.WithRefreshAhead(System.TimeSpan.FromSeconds(20));
        });

        var snapshots = store.GetSnapshots();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("Sample.Method", snapshot.MethodId);
        Assert.Equal(System.TimeSpan.FromMinutes(2), snapshot.Policy.Duration);
        Assert.Contains("runtime", snapshot.Policy.Tags);
        Assert.Equal(System.TimeSpan.FromSeconds(20), snapshot.Policy.RefreshAhead);
        var fields = CachePolicyMapper.DetectFields(snapshot.Policy);
        Assert.Equal(CachePolicyFields.Duration |
                     CachePolicyFields.Tags |
                     CachePolicyFields.RefreshAhead,
            fields);
    }

    [Fact]
    public async Task UpsertAsync_WithEntryOptionsBuilder_StoresPolicy()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Sample.Entry", builder =>
        {
            builder.WithDuration(System.TimeSpan.FromMinutes(3));
            builder.WithTags("runtime", "docs");
        });

        var snapshots = store.GetSnapshots();
        var snapshot = Assert.Single(snapshots);
        Assert.Equal("Sample.Entry", snapshot.MethodId);
        Assert.Equal(System.TimeSpan.FromMinutes(3), snapshot.Policy.Duration);
        Assert.Contains("runtime", snapshot.Policy.Tags);
        Assert.Contains("docs", snapshot.Policy.Tags);
    }

    [Fact]
    public async Task ApplyAsync_WithFluentConfiguration_StoresMethodOverrides()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.ApplyAsync(fluent =>
        {
            fluent.ForService<IRuntimeDocService>()
                .Method(x => x.GetAsync(default))
                .Configure(options => options.WithDuration(System.TimeSpan.FromSeconds(30)).WithTags("runtime", "users"));

            fluent.ForService<IRuntimeDocService>()
                .Method(x => x.SearchAsync(default!))
                .Configure(options => options.WithDuration(System.TimeSpan.FromMinutes(2)));
        });

        var snapshots = store.GetSnapshots();
        Assert.Equal(2, snapshots.Count);

        var typeName = "[MethodCache.Core.Tests]" + (typeof(IRuntimeDocService).FullName ?? nameof(IRuntimeDocService)).Replace('+', '.');
        var getMethodId = $"{typeName}.{nameof(IRuntimeDocService.GetAsync)}";
        var searchMethodId = $"{typeName}.{nameof(IRuntimeDocService.SearchAsync)}";

        var getSnapshot = Assert.Single(snapshots, s => s.MethodId == getMethodId);
        Assert.Equal(System.TimeSpan.FromSeconds(30), getSnapshot.Policy.Duration);
        Assert.Contains("runtime", getSnapshot.Policy.Tags);

        var searchSnapshot = Assert.Single(snapshots, s => s.MethodId == searchMethodId);
        Assert.Equal(System.TimeSpan.FromMinutes(2), searchSnapshot.Policy.Duration);
    }

    [Fact]
    public async Task RemoveAsync_RemovesOnlyRequestedOverride()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Method.One", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(1)));
        await configurator.UpsertAsync("Method.Two", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(2)));

        await configurator.RemoveAsync("Method.One");

        var snapshots = store.GetSnapshots();
        var remaining = Assert.Single(snapshots);
        Assert.Equal("Method.Two", remaining.MethodId);
    }

    [Fact]
    public async Task ClearAsync_RemovesAllOverrides()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync("Method.One", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(1)));
        await configurator.UpsertAsync("Method.Two", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(2)));

        await configurator.ClearAsync();

        Assert.Empty(store.GetSnapshots());
    }

    [Fact]
    public async Task UpsertBatchAsync_StoresAllEntries()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        var entries = new[]
        {
            new RuntimeOverrideEntry("Batch.One", CachePolicy.Empty with { Duration = System.TimeSpan.FromMinutes(1) }, CachePolicyFields.Duration),
            new RuntimeOverrideEntry("Batch.Two", CachePolicy.Empty with { Duration = System.TimeSpan.FromMinutes(2), Tags = new[] { "runtime" } }, CachePolicyFields.Duration | CachePolicyFields.Tags)
        };

        await configurator.UpsertBatchAsync(entries);

        var snapshots = await configurator.GetOverridesAsync();
        Assert.Equal(2, snapshots.Count);
    }

    [Fact]
    public async Task GetOverrideAsync_ReturnsNull_WhenMethodIsMissing()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);

        var snapshot = await configurator.GetOverrideAsync("Missing.Method");

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task UpsertAsync_WithMetadataAndExpiration_ExpiresOverride()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new RuntimePolicyStore(time);
        var configurator = new RuntimeCacheConfigurator(store);
        var metadata = new RuntimeOverrideMetadata
        {
            Owner = "ops-team",
            Reason = "incident mitigation",
            Ticket = "INC-1234",
            ExpiresAt = time.GetUtcNow().AddMilliseconds(150)
        };

        await configurator.UpsertAsync(
            "Expiring.Method",
            (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromSeconds(30)).WithTags("runtime"),
            metadata);

        var current = await configurator.GetOverrideAsync("Expiring.Method");
        Assert.NotNull(current);
        Assert.Equal("ops-team", current!.Policy.Metadata["runtime.owner"]);
        Assert.Equal("incident mitigation", current.Policy.Metadata["runtime.reason"]);

        time.Advance(TimeSpan.FromMilliseconds(150));

        var expired = await configurator.GetOverrideAsync("Expiring.Method");
        Assert.Null(expired);
    }

    [Fact]
    public async Task UpsertAsync_WithExpiration_AppliesUntilJustBeforeExpiresAt_AndNotFromExpiresAtOn()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new RuntimePolicyStore(time);
        var configurator = new RuntimeCacheConfigurator(store);
        var expiresAt = time.GetUtcNow().AddMinutes(5);

        await configurator.UpsertAsync(
            "Expiring.Method",
            (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromSeconds(30)),
            new RuntimeOverrideMetadata { Owner = "ops", ExpiresAt = expiresAt });

        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.NotNull(await configurator.GetOverrideAsync("Expiring.Method"));

        time.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await configurator.GetOverrideAsync("Expiring.Method"));
    }

    [Fact]
    public async Task ExpiryTimer_RemovesTheOverride_AndRaisesARemovedChange_WithoutAnyRead()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new RuntimePolicyStore(time);
        var configurator = new RuntimeCacheConfigurator(store);
        var changes = new List<PolicyChange>();
        store.Changed += changes.Add;

        await configurator.UpsertAsync(
            "Short.Method",
            (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromSeconds(30)),
            new RuntimeOverrideMetadata { ExpiresAt = time.GetUtcNow().AddMinutes(1) });
        await configurator.UpsertAsync(
            "Long.Method",
            (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromSeconds(30)),
            new RuntimeOverrideMetadata { ExpiresAt = time.GetUtcNow().AddMinutes(10) });
        changes.Clear();

        time.Advance(TimeSpan.FromMinutes(1));
        var removed = Assert.Single(changes);
        Assert.Equal("Short.Method", removed.MethodId);
        Assert.Equal(PolicyChangeReason.Removed, removed.Reason);

        // The timer is re-armed for the next override, which still applies until its own ExpiresAt.
        time.Advance(TimeSpan.FromMinutes(9) - TimeSpan.FromTicks(1));
        Assert.Single(changes);
        time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(2, changes.Count);
        Assert.Equal("Long.Method", changes[1].MethodId);
    }

    [Fact]
    public async Task UpsertAsync_RenewingAnOverride_KeepsItPastTheOldExpiresAt()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var store = new RuntimePolicyStore(time);
        var configurator = new RuntimeCacheConfigurator(store);

        await configurator.UpsertAsync(
            "Renewed.Method",
            (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromSeconds(30)),
            new RuntimeOverrideMetadata { ExpiresAt = time.GetUtcNow().AddMinutes(1) });
        await configurator.UpsertAsync(
            "Renewed.Method",
            (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromSeconds(30)),
            new RuntimeOverrideMetadata { ExpiresAt = time.GetUtcNow().AddMinutes(5) });

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.NotNull(await configurator.GetOverrideAsync("Renewed.Method"));

        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await configurator.GetOverrideAsync("Renewed.Method"));
    }

    [Fact]
    public async Task RemoveAsync_WithTypedExpression_RemovesOverride()
    {
        var store = new RuntimePolicyStore();
        IRuntimeCacheConfigurator configurator = new RuntimeCacheConfigurator(store);

        var typeName = "[MethodCache.Core.Tests]" + (typeof(IRuntimeDocService).FullName ?? nameof(IRuntimeDocService)).Replace('+', '.');
        var methodId = $"{typeName}.{nameof(IRuntimeDocService.GetAsync)}";

        await configurator.UpsertAsync(methodId, (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(1)));
        await configurator.RemoveAsync<IRuntimeDocService, string>(x => x.GetAsync(default));

        var snapshots = await configurator.GetOverridesAsync();
        Assert.Empty(snapshots);
    }

    [Fact]
    public async Task WatchAsync_EmitsAddedAndRemovedChanges()
    {
        var store = new RuntimePolicyStore();
        var configurator = new RuntimeCacheConfigurator(store);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var changes = new List<PolicyChange>();

        // A watcher sees the changes made after it starts, so start it before changing anything
        await using var watcher = configurator.WatchAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var next = watcher.MoveNextAsync();

        await configurator.UpsertAsync("Watch.Method", (CacheEntryOptions.Builder builder) => builder.WithDuration(System.TimeSpan.FromMinutes(1)));
        await configurator.RemoveAsync("Watch.Method");

        while (changes.Count < 2 && await next)
        {
            changes.Add(watcher.Current);
            if (changes.Count < 2)
            {
                next = watcher.MoveNextAsync();
            }
        }

        Assert.Equal(2, changes.Count);
        Assert.Equal(PolicyChangeReason.Added, changes[0].Reason);
        Assert.Equal(PolicyChangeReason.Removed, changes[1].Reason);
    }
}
