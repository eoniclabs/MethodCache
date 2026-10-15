using MethodCache.Core.Runtime.Distributed;
using MethodCache.Core.Tests.FaultInjection;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Core;

public class PolicyTransitionTests
{
    private static readonly string MethodId = typeof(IReader).FullName!.Replace('+', '.') + ".ReadAsync";
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly MethodCache.TestSupport.AtomicTestCache _store = new();

    private ServiceProvider Node(Reader origin, Action<MethodCacheBuilder>? configure = null, IBackplane? backplane = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IReader>(origin);
        if (backplane != null) services.AddSingleton(backplane);
        services.AddMethodCache(builder => { builder.Defaults(o => o.WithDuration(TimeSpan.FromMinutes(5))); configure?.Invoke(builder); });
        services.AddMethodCacheDistributedCache(_ => _store, o => { o.ClockSkewTolerance = TimeSpan.Zero; o.TagCheckInterval = TimeSpan.Zero; });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task EqualDeclarations_ShareL2_AuditAndIdenticalPredicateDoNotRetire()
    {
        Func<int, bool> predicate = static value => value > 0;
        void Configure(MethodCacheBuilder b) => b.Method((IReader r) => r.ReadAsync(0)).CacheWhen(predicate).Configure(o => o.WithTags("a", "b"));
        var writer = new Reader(100); var receiver = new Reader(200);
        await using var first = Node(writer, Configure);
        await using var second = Node(receiver, Configure);
        Assert.Equal(101, await first.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(101, await second.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(0, receiver.Calls);
        var before = PolicyHandle.For(first.GetRequiredService<MethodCache.Policies.IPolicyRegistry>(), MethodId).Current;
        await first.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId,
            o => o.CacheWhen(predicate).WithTags("b", "a", "a"), new RuntimeOverrideMetadata(Owner: "review"));
        var after = PolicyHandle.For(first.GetRequiredService<MethodCache.Policies.IPolicyRegistry>(), MethodId).Current;
        Assert.Same(before.Generation, after.Generation);
        Assert.Equal(before.Identity, after.Identity);
        Assert.False(after.PrivateIdentity);
        Assert.Equal(101, await first.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal("review", (await first.GetRequiredService<IRuntimeCacheConfigurator>().GetOverridesAsync()).Single().Metadata!.Owner);
    }

    [Fact]
    public async Task RuntimeOverrides_ArePrivate_RemoveReturnsToInvalidatedSharedNamespace()
    {
        var hub = new ChaosBackplaneHub(_time);
        await using var first = Node(new Reader(100), backplane: hub.Join());
        await using var second = Node(new Reader(200), backplane: hub.Join());
        Assert.Equal(101, await first.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(101, await second.GetRequiredService<IReader>().ReadAsync(1));
        var runtime = first.GetRequiredService<IRuntimeCacheConfigurator>();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await runtime.UpsertAsync(MethodId, o => o.WithDuration(TimeSpan.FromSeconds(10)));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(102, await first.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(201, await second.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(TimeSpan.FromMinutes(5), second.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == MethodId).Duration);
        await using var third = Node(new Reader(300));
        await third.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithDuration(TimeSpan.FromSeconds(10)));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(301, await third.GetRequiredService<IReader>().ReadAsync(1));
        var a = PolicyHandle.For(first.GetRequiredService<MethodCache.Policies.IPolicyRegistry>(), MethodId).Current;
        var b = PolicyHandle.For(third.GetRequiredService<MethodCache.Policies.IPolicyRegistry>(), MethodId).Current;
        Assert.NotEqual(a.Identity, b.Identity);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await runtime.RemoveAsync(MethodId);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(103, await first.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(103, await second.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal("shared", first.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == MethodId).L2Namespace);
    }

    [Fact]
    public async Task DisabledGeneration_DoesNotJoinOldFlight_OrPublishItsCompletion()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = new Reader(100) { FirstLoad = () => { started.SetResult(); return pending.Task; } };
        await using var node = Node(origin);
        var reader = node.GetRequiredService<IReader>();
        var old = reader.ReadAsync(1);
        await started.Task;
        await node.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithEnabled(false));
        Assert.Equal(102, await reader.ReadAsync(1));
        Assert.Equal(103, await reader.ReadAsync(1));
        pending.SetResult(101);
        Assert.Equal(101, await old);
        Assert.Empty(node.GetRequiredService<ICacheAdministrator>().ListEntries());
        await node.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithEnabled(true));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(104, await reader.ReadAsync(1));
        Assert.Equal(104, await reader.ReadAsync(1));
    }

    [Fact]
    public async Task UnknownRuntimeId_AndCanceledMutation_LeaveOverridesEmpty()
    {
        await using var node = Node(new Reader(100));
        var runtime = node.GetRequiredService<IRuntimeCacheConfigurator>();
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.UpsertAsync("typo", o => o.WithEnabled(false)));
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.RemoveAsync("typo"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.UpsertAsync(MethodId, o => o.WithEnabled(false), cancellationToken: new CancellationToken(true)));
        Assert.Empty(await runtime.GetOverridesAsync());
    }

    [Fact]
    public async Task ConfigReload_NewIdentity_CannotReadOldConfigurationWrites()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [$"MethodCache:Services:{MethodId}:Duration"] = "00:05:00" });
        await using var writer = Node(new Reader(100));
        await using var receiver = Node(new Reader(200), b => b.FromConfiguration(configuration, "MethodCache"));
        Assert.Equal(101, await writer.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(101, await receiver.GetRequiredService<IReader>().ReadAsync(1));
        configuration[$"MethodCache:Services:{MethodId}:Duration"] = "00:00:01";
        ((IConfigurationRoot)configuration).Reload();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(201, await receiver.GetRequiredService<IReader>().ReadAsync(1));
        Assert.Equal(102, await writer.GetRequiredService<IReader>().ReadAsync(2));
        Assert.Equal(202, await receiver.GetRequiredService<IReader>().ReadAsync(2));
    }

    [Fact]
    public async Task ApplicationTag_ImitatingMethodMetadata_DoesNotRetireTheMethod()
    {
        var hub = new ChaosBackplaneHub(_time);
        await using var node = Node(new Reader(100), backplane: hub.Join());
        var reader = node.GetRequiredService<IReader>();
        Assert.Equal(101, await reader.ReadAsync(1));
        var handle = PolicyHandle.For(node.GetRequiredService<MethodCache.Policies.IPolicyRegistry>(), MethodId);
        var generation = handle.Current.Generation;
        var forged = DistributedCacheLayer.MethodTag(MethodId);
        var cache = node.GetRequiredService<ICacheManager>();
        await cache.GetOrCreateAsync("app", (_, _) => ValueTask.FromResult(1), o => o.WithTags(forged));
        await cache.InvalidateTagAsync(forged);
        Assert.False((await cache.TryGetAsync<int>("app")).Found);
        Assert.Same(generation, handle.Current.Generation);
        Assert.Equal(101, await reader.ReadAsync(1));
    }

    [Fact]
    public async Task ApplicationKey_ImitatingPolicyNamespace_CannotReplaceAMethodValue()
    {
        await using var first = Node(new Reader(100));
        Assert.Equal(101, await first.GetRequiredService<IReader>().ReadAsync(1));
        var identity = PolicyHandle.For(first.GetRequiredService<MethodCache.Policies.IPolicyRegistry>(), MethodId).Current.Identity;
        var logicalKey = MethodCacheKeys.For(typeof(IReader), nameof(IReader.ReadAsync), MethodCacheKeys.Part(1));
        var forged = "\0policy:" + logicalKey.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + logicalKey + ":" + identity;
        await first.GetRequiredService<ICacheManager>().GetOrCreateAsync(forged, (_, _) => ValueTask.FromResult(999));
        await using var receiver = Node(new Reader(200));
        Assert.Equal(101, await receiver.GetRequiredService<IReader>().ReadAsync(1));
    }

    [Fact]
    public async Task ConfiguredUnknownId_IsStillRejectedByRuntimeConfiguration()
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["MethodCache:Services:typo:Duration"] = "00:01:00" }).Build();
        await using var node = Node(new Reader(100), b => b.FromConfiguration(settings, "MethodCache"));
        var runtime = node.GetRequiredService<IRuntimeCacheConfigurator>();
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.UpsertAsync("typo", o => o.WithEnabled(false)));
        Assert.Empty(await runtime.GetOverridesAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task BehaviorChange_RetiresPendingLoads_BeforeReturning(int setting)
    {
        Action<CacheEntryOptions.Builder> change = setting switch
        {
            0 => o => o.WithDuration(TimeSpan.FromSeconds(1)),
            1 => o => o.WithSlidingExpiration(TimeSpan.FromSeconds(1)),
            2 => o => o.RefreshAhead(TimeSpan.FromSeconds(1)),
            3 => o => o.FailSafe(TimeSpan.FromSeconds(5)),
            4 => o => o.FailSafe(TimeSpan.Zero, timeout: TimeSpan.FromSeconds(1)),
            5 => o => o.FailSafe(TimeSpan.Zero, retryAfter: TimeSpan.FromSeconds(1)),
            6 => o => o.WithTags("changed"),
            7 => o => o.WithVersion(2),
            8 => o => o.WithDistributed(false),
            9 => o => o.CacheWhen<int>(value => value > 0),
            10 => o => o.WithEnabled(false),
            _ => o => o.ClearTags()
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var origin = new Reader(100) { FirstLoad = () => { started.SetResult(); return release.Task; } };
        await using var node = Node(origin, setting == 11 ? b => b.Method((IReader r) => r.ReadAsync(0)).Configure(o => o.WithTags("initial")) : null);
        var reader = node.GetRequiredService<IReader>();
        var pending = reader.ReadAsync(1);
        await started.Task;
        await node.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, change);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(102, await reader.ReadAsync(1));
        release.SetResult(101);
        Assert.Equal(101, await pending);
        Assert.Equal(setting == 10 ? 103 : 102, await reader.ReadAsync(1));
    }

    [Fact]
    public async Task LostMethodMessage_LeavesRemotePolicyAndL1UntilExpiry_WithoutCrossingPrivateNamespace()
    {
        var hub = new ChaosBackplaneHub(_time);
        await using var first = Node(new Reader(100), backplane: hub.Join());
        await using var second = Node(new Reader(200), backplane: hub.Join());
        var a = first.GetRequiredService<IReader>();
        var b = second.GetRequiredService<IReader>();
        Assert.Equal(101, await a.ReadAsync(1));
        Assert.Equal(101, await b.ReadAsync(1));
        hub.Mode = BackplaneChaos.Drop;
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await first.GetRequiredService<IRuntimeCacheConfigurator>().UpsertAsync(MethodId, o => o.WithDuration(TimeSpan.FromSeconds(1)));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(102, await a.ReadAsync(1));
        Assert.Equal(101, await b.ReadAsync(1));
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(103, await a.ReadAsync(1));
        Assert.Equal(101, await b.ReadAsync(1));
        Assert.True(hub.Dropped > 0);
        Assert.Equal(TimeSpan.FromMinutes(5), second.GetRequiredService<ICacheAdministrator>().GetMethods().Single(m => m.MethodId == MethodId).Duration);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(201, await b.ReadAsync(1));
    }

    public interface IReader { [Cache] Task<int> ReadAsync(int id); }
    public sealed class Reader(int basis) : IReader
    {
        public int Calls;
        public Func<Task<int>>? FirstLoad;
        public Task<int> ReadAsync(int id)
        {
            var calls = Interlocked.Increment(ref Calls);
            return calls == 1 && FirstLoad != null ? FirstLoad() : Task.FromResult(basis + calls);
        }
    }
}
