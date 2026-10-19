using MethodCache.Core.Configuration.Surfaces.Runtime;
using MethodCache.Core.PolicyPipeline.Resolution;
using MethodCache.Core.PolicyPipeline.Sources;
using MethodCache.Policies;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MethodCache.Core.Tests.Configuration;

public class RuntimePolicyOrderingTests
{
    private const string Method = "Ordering.Read";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedUpsertNotification_CannotReplaceANewerMutation(bool remove)
    {
        using var store = new RuntimePolicyStore(new FakeTimeProvider());
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += change =>
        {
            if (change.Delta.Snapshot?.Duration == TimeSpan.FromSeconds(1))
            {
                started.TrySetResult();
                release.Wait();
            }
        };
        await using var resolver = Resolver(store);
        await resolver.ResolveAsync(Method);
        var older = Task.Run(() => store.UpsertAsync(Method, Policy(1), CachePolicyFields.Duration));
        try
        {
            await started.Task;
            if (remove) await store.RemoveAsync(Method);
            else await store.UpsertAsync(Method, Policy(2), CachePolicyFields.Duration);
        }
        finally { release.Set(); }
        await older;
        Assert.Equal(store.GetSnapshot(Method)?.Policy.Duration, (await resolver.ResolveAsync(Method)).Policy.Duration);
    }

    [Fact]
    public async Task ReentrantUpsertNotification_CannotReplaceANewerOverride()
    {
        using var store = new RuntimePolicyStore(new FakeTimeProvider());
        store.Changed += change =>
        {
            if (change.Delta.Snapshot?.Duration == TimeSpan.FromSeconds(1))
            {
                store.UpsertAsync(Method, Policy(2), CachePolicyFields.Duration);
            }
        };
        await using var resolver = Resolver(store);
        await resolver.ResolveAsync(Method);
        await store.UpsertAsync(Method, Policy(1), CachePolicyFields.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), (await resolver.ResolveAsync(Method)).Policy.Duration);
    }

    [Fact]
    public async Task Initialization_EmptySnapshot_IgnoresAnOlderPendingAddition()
    {
        using var store = new RuntimePolicyStore(new FakeTimeProvider());
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += change =>
        {
            if (change.Delta.Snapshot?.Duration == TimeSpan.FromSeconds(1))
            {
                started.TrySetResult();
                release.Wait();
            }
        };
        var pending = Task.Run(() => store.UpsertAsync(Method, Policy(1), CachePolicyFields.Duration));
        await started.Task;
        await store.RemoveAsync(Method);
        await using var resolver = Resolver(store);
        try { await resolver.ResolveAsync(Method); }
        finally { release.Set(); }
        await pending;
        Assert.Null(store.GetSnapshot(Method));
        Assert.Null((await resolver.ResolveAsync(Method)).Policy.Duration);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("clear")]
    [InlineData("expire")]
    public async Task DelayedRemovalNotification_CannotRemoveARenewedOverride(string operation)
    {
        var time = new FakeTimeProvider();
        using var store = new RuntimePolicyStore(time);
        await store.UpsertAsync(Method, Policy(1), CachePolicyFields.Duration,
            operation == "expire" ? new RuntimeOverrideMetadata { ExpiresAt = time.GetUtcNow() + TimeSpan.FromSeconds(1) } : null);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += change =>
        {
            if (change.Reason == PolicyChangeReason.Removed)
            {
                started.TrySetResult();
                release.Wait();
            }
        };
        await using var resolver = Resolver(store);
        await resolver.ResolveAsync(Method);
        var removal = Task.Run(async () =>
        {
            if (operation == "remove") await store.RemoveAsync(Method);
            else if (operation == "clear") await store.ClearAsync();
            else time.Advance(TimeSpan.FromSeconds(1));
        });
        try
        {
            await started.Task;
            await store.UpsertAsync(Method, Policy(2), CachePolicyFields.Duration);
        }
        finally { release.Set(); }
        await removal;
        Assert.Equal(TimeSpan.FromSeconds(2), store.GetSnapshot(Method)?.Policy.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), (await resolver.ResolveAsync(Method)).Policy.Duration);
    }

    [Fact]
    public async Task DelayedChange_ForAnotherMethod_RemainsApplicable()
    {
        using var store = new RuntimePolicyStore(new FakeTimeProvider());
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += change =>
        {
            if (change.MethodId == Method)
            {
                started.TrySetResult();
                release.Wait();
            }
        };
        await using var resolver = Resolver(store);
        await resolver.ResolveAsync(Method);
        var pending = Task.Run(() => store.UpsertAsync(Method, Policy(1), CachePolicyFields.Duration));
        try
        {
            await started.Task;
            await store.UpsertAsync("Another.Read", Policy(2), CachePolicyFields.Duration);
        }
        finally { release.Set(); }
        await pending;
        Assert.Equal(TimeSpan.FromSeconds(1), (await resolver.ResolveAsync(Method)).Policy.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), (await resolver.ResolveAsync("Another.Read")).Policy.Duration);
    }

    [Fact]
    public async Task SameRuntimeSource_RegisteredTwice_UpdatesEveryLayer()
    {
        using var store = new RuntimePolicyStore(new FakeTimeProvider());
        await store.UpsertAsync(Method, Policy(1), CachePolicyFields.Duration);
        var source = new RuntimePolicySource(store);
        await using var resolver = new PolicyResolver([
            new PolicySourceRegistration(source, 100),
            new PolicySourceRegistration(source, 100)]);
        await resolver.ResolveAsync(Method);
        await store.UpsertAsync(Method, Policy(2), CachePolicyFields.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), (await resolver.ResolveAsync(Method)).Policy.Duration);
    }

    private static CachePolicy Policy(int seconds) => CachePolicy.Empty with { Duration = TimeSpan.FromSeconds(seconds) };
    private static PolicyResolver Resolver(RuntimePolicyStore store) => new([new PolicySourceRegistration(new RuntimePolicySource(store), 100)]);
}
