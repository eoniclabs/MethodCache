using MethodCache.Extensibility;
using MethodCache.Generated;
using MethodCache.Infrastructure;
using MethodCache.Policies;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using BoundMethod = MethodCache.Generated.CachedMethod;

namespace MethodCache.Core.Tests.Core;

[Collection(nameof(CpuIntensiveCollection))]
public class GeneratedContractTests
{
    private interface IService { }

    private static (BoundMethod Method, PolicyHandle Handle, InMemoryCacheManager Memory) Create(string name = "Get")
    {
        var policy = CacheRuntimePolicy.Empty(name) with { Version = 1, Duration = TimeSpan.FromMinutes(5) };
        var handle = PolicyHandle.Fixed(policy);
        var memory = new InMemoryCacheManager();
        var services = new DecoratorServices(memory, Substitute.For<IPolicyRegistry>(), new FastHashKeyGenerator());
        return (new BoundMethod(services, handle, typeof(IService), name, name), handle, memory);
    }

    [Fact]
    public void Register_UnknownVersion_ThrowsAtStartupBeforeDescribe()
    {
        var module = new UnknownModule();
        try
        {
            GeneratedCode.Register(99, module);
            Assert.Equal(0, module.Calls);
            var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMethodCache());
            Assert.Contains("contract 99", error.Message);
            Assert.Contains(typeof(UnknownModule).Assembly.GetName().Name!, error.Message);
            Assert.Equal(0, module.Calls);
        }
        finally { GeneratedCode.Unregister(typeof(UnknownModule)); }
    }

    private sealed class UnknownModule : GeneratedModule
    {
        internal int Calls;
        public override void Describe(GeneratedModuleBuilder builder) { Calls++; throw new InvalidOperationException("Must not run."); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(256)]
    public void StartKey_GrowsFromCallerBuffer_PreservesEscapingAndUnicode(int capacity)
    {
        var (method, _, memory) = Create(new string('x', 500));
        using var owned = memory;
        var call = method.Capture();
        var key = call.StartKey(new char[capacity]);
        key.AppendPart("a:\\b\0\ud800\udfff");
        key.AppendPart(int.MinValue);
        key.AppendNullPart();
        var expected = MethodCacheKeys.ForVersion(typeof(IService), new string('x', 500), 1,
            MethodCacheKeys.Part("a:\\b\0\ud800\udfff"), MethodCacheKeys.Part(int.MinValue), MethodCacheKeys.Null);
        Assert.Equal(expected, key.AsSpan().ToString());
        Assert.False(call.TryGetValue<string>(ref key, out _));
        Assert.Equal(expected, key.ToString());
    }

    [Fact]
    public void Lookup_BuilderFromAnotherBinding_RejectsBeforeAccess()
    {
        var (first, _, firstMemory) = Create();
        var (second, _, secondMemory) = Create();
        using var firstOwned = firstMemory;
        using var secondOwned = secondMemory;
        var firstCall = first.Capture();
        var secondCall = second.Capture();
        Assert.Throws<ArgumentException>(() =>
        {
            var key = firstCall.StartKey(stackalloc char[64]);
            secondCall.TryGetValue<string>(ref key, out _);
        });
    }

    private static void Update(PolicyHandle handle)
        => handle.Update(new PolicyResolutionResult(handle.MethodId,
            CachePolicy.Empty with { Version = 2, Duration = TimeSpan.FromMinutes(1) },
            [], DateTimeOffset.UtcNow, version: 2));

    [Fact]
    public async Task Capture_PolicyChangesBeforeProbeAndLoad_OldSnapshotBypassesPublication()
    {
        var (method, handle, memory) = Create();
        using var owned = memory;
        var old = method.Capture();
        Update(handle);
        var key = old.StartKey(stackalloc char[64]);
        key.AppendPart(42);
        Assert.False(old.TryGetTask<string>(ref key, out _));
        var text = key.ToString();
        Assert.Equal("old", await old.LoadAsync(text, 0, static (_, _) => Task.FromResult("old"), [], default));
        Assert.False(memory.TryGetFast<string>(text, out _));
        Assert.Contains(":v2", method.Capture().KeyPrefix);
    }

    [Fact]
    public async Task Load_PolicyChangesWhileOriginRuns_DoesNotRepublishOldValue()
    {
        var (method, handle, memory) = Create();
        using var owned = memory;
        var old = method.Capture();
        var oldKey = old.KeyPrefix;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = old.LoadAsync(oldKey, (started, release), static (_, state) =>
        {
            state.started.SetResult();
            return state.release.Task;
        }, [], default);
        await started.Task;
        Update(handle);
        var current = method.Capture();
        Assert.Equal("new", await current.LoadAsync(current.KeyPrefix, 0, static (_, _) => Task.FromResult("new"), [], default));
        release.SetResult("old");
        Assert.Equal("old", await pending);
        Assert.False(memory.TryGetFast<string>(oldKey, out _));
        Assert.True(memory.TryGetFast<string>(current.KeyPrefix, out var value));
        Assert.Equal("new", value);
    }

    [Fact]
    public async Task Load_NewGenerationWithSameKey_ReplacesRetiredFlightAndRejectsOldPublication()
    {
        var (method, handle, memory) = Create();
        using var owned = memory;
        var old = method.Capture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = old.LoadAsync(old.KeyPrefix, (started, release), static (_, state) =>
        {
            state.started.SetResult();
            return state.release.Task;
        }, [], default);
        await started.Task;
        handle.Update(new PolicyResolutionResult(handle.MethodId,
            CachePolicy.Empty with { Version = 1, Duration = TimeSpan.FromMinutes(1) }, [], DateTimeOffset.UtcNow, version: 2));
        var current = method.Capture();
        Assert.Equal(old.KeyPrefix, current.KeyPrefix);
        Assert.Equal("new", await current.LoadAsync(current.KeyPrefix, 0, static (_, _) => Task.FromResult("new"), [], default));
        release.SetResult("old");
        Assert.Equal("old", await pending);
        var key = current.StartKey(stackalloc char[256]);
        Assert.True(current.TryGetValue<string>(ref key, out var value));
        Assert.Equal("new", value);
    }

    [Fact]
    public void Lookup_PolicyChangesWithoutKeyVersion_RejectsPreviousPublishedEntry()
    {
        var (method, handle, memory) = Create();
        using var owned = memory;
        var old = method.Capture();
        Assert.Equal(7, old.Load(old.KeyPrefix, 0, static (_, _) => 7, [], default));
        handle.Update(new PolicyResolutionResult(handle.MethodId,
            CachePolicy.Empty with { Version = 1, Duration = TimeSpan.FromMinutes(1) }, [], DateTimeOffset.UtcNow, version: 2));
        var current = method.Capture();
        Assert.Equal(old.KeyPrefix, current.KeyPrefix);
        var key = current.StartKey(stackalloc char[256]);
        Assert.False(current.TryGetValue<int>(ref key, out _));
        Assert.False(memory.TryGetFast<int>(old.KeyPrefix, out _));
        Assert.Equal(99, current.Load(current.KeyPrefix, 0, static (_, _) => 99, [], default));
        Assert.True(current.TryGetValue<int>(ref key, out var value));
        Assert.Equal(99, value);
    }

    [Fact]
    public async Task Capture_TwoContainers_UpdatesAndDisposalStayIsolated()
    {
        var (first, firstHandle, firstMemory) = Create();
        var (second, _, secondMemory) = Create();
        using var secondOwned = secondMemory;
        var call = second.Capture();
        Assert.Equal(7, await call.LoadAsync(call.KeyPrefix, 0, static (_, _) => Task.FromResult(7), [], default));
        Update(firstHandle);
        firstMemory.Dispose();
        Assert.Contains(":v2", first.Capture().KeyPrefix);
        var key = call.StartKey(stackalloc char[64]);
        Assert.True(call.TryGetValue<int>(ref key, out var value));
        Assert.Equal(7, value);
    }

    [Fact]
    public void Load_SynchronousOrigin_CachesWithoutWaitingOnTask()
    {
        var (method, _, memory) = Create();
        using var owned = memory;
        var call = method.Capture();
        var calls = new Counter();
        static int Load(MethodCall _, Counter state) => ++state.Calls;
        Assert.Equal(1, call.Load(call.KeyPrefix, calls, Load, [], default));
        Assert.Equal(1, call.Load(call.KeyPrefix, calls, Load, [], default));
        Assert.Equal(1, calls.Calls);
    }
    private sealed class Counter { internal int Calls; }

    [Fact]
    public void TypeArguments_StaticIdentity_ReusesBoundSnapshotAndRejectsDifferentTypes()
    {
        var (method, _, memory) = Create();
        using var owned = memory;
        var captured = method.Capture();
        var first = captured.WithTypeArguments("<System.Int32>");
        var repeated = captured.WithTypeArguments("<System.Int32>");
        var key = first.StartKey(stackalloc char[256]);
        Assert.False(repeated.TryGetTask<int>(ref key, out _));
        Assert.Throws<ArgumentException>(() =>
        {
            var different = captured.WithTypeArguments("<System.String>");
            var original = first.StartKey(stackalloc char[256]);
            different.TryGetTask<int>(ref original, out _);
        });
    }

    [Fact]
    public void TextLookup_OwnedKey_ValidatesBindingAndRetirementWithoutWritableSpan()
    {
        var (method, handle, memory) = Create();
        using var owned = memory;
        var call = method.Capture();
        var text = call.BindTextKey(call.KeyPrefix);
        Assert.Equal(7, call.Load(text.ToString(), 0, static (_, _) => 7, [], default));
        Assert.True(call.TryGetTask<int>(in text, out var task));
        Assert.Equal(7, task.Result);
        Assert.True(call.TryGetValueTask<int>(in text, out var valueTask));
        Assert.Equal(7, valueTask.Result);
        Update(handle);
        Assert.False(call.TryGetTask<int>(in text, out _));
        Assert.Throws<ArgumentException>(() => method.Capture().TryGetValue<int>(in text, out _));
        Assert.Equal(call.KeyPrefix, text.ToString());
    }

    [Fact]
    public async Task Load_SynchronousFactoryRequestsSameKeyAsynchronously_ReportsRecursion()
    {
        var (method, _, memory) = Create();
        using var owned = memory;
        var call = method.Capture();
        var recursive = new RecursiveCall();
        Assert.Equal(7, call.Load(call.KeyPrefix, recursive, static (invocation, state) =>
        {
            state.Operation = invocation.LoadAsync(invocation.KeyPrefix, 0, static (_, _) => Task.FromResult(99), [], default);
            return 7;
        }, [], default));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => recursive.Operation!);
        Assert.Contains("wait for itself", error.Message);
    }
    private sealed class RecursiveCall { internal Task<int>? Operation; }

    [Fact]
    public void Load_CustomAsyncManager_SynchronousInvocationBypassesIt()
    {
        var manager = Substitute.For<ICacheManager>();
        var services = new DecoratorServices(manager, Substitute.For<IPolicyRegistry>(), new FastHashKeyGenerator());
        var method = new BoundMethod(services, PolicyHandle.Fixed(CacheRuntimePolicy.Empty("Get")), typeof(IService), "Get", "Get");
        var call = method.Capture();
        var key = call.StartKey(stackalloc char[256]);
        Assert.False(call.TryGetValue<int>(ref key, out _));
        Assert.Equal(7, call.Load(key.ToString(), 0, static (_, _) => 7, [], default));
        Assert.Empty(manager.ReceivedCalls());
    }

    [Fact]
    public void Lookup_FallbackBuilderAndExactDirtyBuffer_PreserveBindingAndWrittenText()
    {
        var (method, handle, memory) = Create();
        using var owned = memory;
        var call = method.Capture();
        var expected = MethodCacheKeys.ForVersion(typeof(IService), "Get", 1, MethodCacheKeys.Part(42));
        Span<char> buffer = stackalloc char[expected.Length];
        buffer.Fill('!');
        var inline = call.StartKey(buffer);
        inline.AppendPart(42);
        Assert.Equal(expected, inline.ToString());
        var fallback = call.CreateFallbackKey([42]);
        var fallbackText = fallback.ToString();
        Update(handle);
        Assert.False(call.TryGetValue<int>(ref inline, out _));
        Assert.False(call.TryGetTask<int>(ref fallback, out _));
        Assert.Equal(fallbackText, fallback.ToString());
        Assert.Throws<ArgumentException>(() =>
        {
            var foreign = call.CreateFallbackKey([42]);
            method.Capture().TryGetValue<int>(ref foreign, out _);
        });
    }

    [Fact]
    public async Task Load_SynchronousOriginPolicyRetired_DoesNotPublishOldSnapshot()
    {
        var (method, handle, memory) = Create();
        using var owned = memory;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = method.Capture();
        var pending = Task.Run(() => old.Load(old.KeyPrefix, (started, release), static (_, state) =>
        {
            state.started.SetResult();
            state.release.Wait();
            return 7;
        }, [], default));
        await started.Task;
        Update(handle);
        var current = method.Capture();
        Assert.Equal(99, current.Load(current.KeyPrefix, 0, static (_, _) => 99, [], default));
        release.Set();
        Assert.Equal(7, await pending);
        Assert.False(memory.TryGetFast<int>(old.KeyPrefix, out _));
        Assert.True(memory.TryGetFast<int>(current.KeyPrefix, out var value));
        Assert.Equal(99, value);
    }

    [Fact]
    public async Task Load_AsyncCallerJoinsSynchronousFlight_OriginRunsOnce()
    {
        var (method, _, memory) = Create();
        using var owned = memory;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = method.Capture();
        var calls = new Counter();
        var pending = Task.Run(() => call.Load(call.KeyPrefix, (calls, started, release), static (_, state) =>
        {
            Interlocked.Increment(ref state.calls.Calls);
            state.started.SetResult();
            state.release.Wait();
            return 7;
        }, [], default));
        await started.Task;
        var joined = call.LoadAsync(call.KeyPrefix, calls, static (_, state) => Task.FromResult(Interlocked.Increment(ref state.Calls)), [], default);
        Assert.False(joined.IsCompleted);
        release.Set();
        Assert.Equal(7, await pending);
        Assert.Equal(7, await joined);
        Assert.Equal(1, calls.Calls);
    }

    [Fact]
    public async Task Load_SynchronousFlightInvalidated_DoesNotOverwriteNewPublication()
    {
        var (method, _, memory) = Create();
        using var owned = memory;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = method.Capture();
        var pending = Task.Run(() => call.Load(call.KeyPrefix, (started, release), static (_, state) =>
        {
            state.started.SetResult();
            state.release.Wait();
            return 7;
        }, [], default));
        await started.Task;
        await memory.InvalidateByKeysAsync(call.KeyPrefix);
        Assert.Equal(99, call.Load(call.KeyPrefix, 0, static (_, _) => 99, [], default));
        release.Set();
        Assert.Equal(7, await pending);
        Assert.True(memory.TryGetFast<int>(call.KeyPrefix, out var value));
        Assert.Equal(99, value);
    }

    [Fact]
    public async Task Load_SynchronousOwnerCancels_AsyncJoinerLoadsWithItsOwnToken()
    {
        var (method, _, memory) = Create();
        using var owned = memory;
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = method.Capture();
        var pending = Task.Run(() => call.Load(call.KeyPrefix, (started, release, cancellation.Token), static (_, state) =>
        {
            state.started.SetResult();
            state.release.Wait();
            state.Token.ThrowIfCancellationRequested();
            return 7;
        }, [], cancellation.Token));
        await started.Task;
        var joined = call.LoadAsync(call.KeyPrefix, 0, static (_, _) => Task.FromResult(99), [], default);
        Assert.False(joined.IsCompleted);
        cancellation.Cancel();
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(99, await joined);
    }
}
