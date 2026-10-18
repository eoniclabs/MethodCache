using MethodCache.Core.Runtime.Distributed;
using MethodCache.Extensibility;
using MethodCache.Infrastructure;
using MethodCache.TestSupport;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace MethodCache.Core.Tests.FaultInjection;

public class DistributedScopeFailureTests
{
    [Fact]
    public async Task IncompatibleScope_FailsSoftAndRetainsInvalidationsWithoutRepeatedBackendCalls()
    {
        var backend = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        var epochs = (IDistributedEpochStore)backend;
        var attempts = 0;
        epochs.EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { attempts++; return ValueTask.FromException<TimeSpan>(new IncompatibleDistributedScopeException("incompatible schema")); });
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions());
        Assert.False((await layer.TryGetAsync<int>("key")).Found);
        await layer.RemoveAsync(["key"]);
        await layer.FlushAsync(force: true);
        Assert.False((await layer.TryGetAsync<int>("key")).Found);
        Assert.False(await layer.CheckAvailableAsync(default));
        Assert.Equal(1, attempts);
        Assert.Equal(1, layer.GetInvalidationQueues().Distributed);
    }

    [Fact]
    public async Task ReplacementBeforeScopeValidation_QueuesInvalidationAndFlushesAfterRecovery()
    {
        var clock = new FakeTimeProvider();
        var backend = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        var epochs = (IDistributedEpochStore)backend;
        var unavailable = true;
        epochs.EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => unavailable ? ValueTask.FromException<TimeSpan>(new IOException("offline")) : new ValueTask<TimeSpan>(call.Arg<TimeSpan>()));
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock);
        await layer.WriteAsync("key", 2, null, layer.Now, TimeSpan.FromMinutes(1), replaces: true);
        Assert.Equal(1, layer.GetInvalidationQueues().Distributed);
        unavailable = false;
        await layer.FlushAsync(force: true);
        Assert.Equal(0, layer.GetInvalidationQueues().Distributed);
        await backend.Received().WriteInvalidationStampAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task ReconnectedScope_RevalidatesAndRejectsShorterEstablishedRetention()
    {
        var clock = new FakeTimeProvider();
        var backend = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        var epochs = (IDistributedEpochStore)backend;
        var retention = TimeSpan.FromDays(1);
        var validations = 0;
        epochs.EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => { validations++; return new ValueTask<TimeSpan>(retention); });
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock);
        Assert.True(await layer.CheckAvailableAsync(default));
        backend.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<byte[]?>(new IOException("offline")));
        Assert.False(await layer.CheckAvailableAsync(default));
        retention = TimeSpan.FromHours(1);
        backend.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(null));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await layer.CheckAvailableAsync(default));
        Assert.True(layer.HasIncompatibleScope);
        Assert.Equal(2, validations);
    }

    [Fact]
    public async Task RetentionMismatchHealth_ClearsWhenRecoveryEstablishesTheConfiguredContract()
    {
        var clock = new FakeTimeProvider();
        var backend = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        var epochs = (IDistributedEpochStore)backend;
        var options = new DistributedCacheOptions();
        var established = TimeSpan.FromHours(1);
        epochs.EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<TimeSpan>(established));
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), options, timeProvider: clock);
        Assert.True(await layer.CheckAvailableAsync(default));
        Assert.True(layer.HasRetentionMismatch);
        backend.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<byte[]?>(new IOException("offline")));
        Assert.False(await layer.CheckAvailableAsync(default));
        established = options.TagRetention;
        backend.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(null));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await layer.CheckAvailableAsync(default));
        Assert.False(layer.HasRetentionMismatch);
    }

    [Fact]
    public async Task ScopeValidationFromBeforeFailure_CannotSuppressRecoveryRevalidation()
    {
        var clock = new FakeTimeProvider();
        var backend = Substitute.For<IAtomicInvalidationStore, IDistributedEpochStore>();
        var epochs = (IDistributedEpochStore)backend;
        var oldValidation = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        epochs.EnsureRetentionContractAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++validations == 1 ? new ValueTask<TimeSpan>(oldValidation.Task)
                : new ValueTask<TimeSpan>(validations == 2 ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1)));
        await using var layer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock);
        var oldRead = layer.CheckAvailableAsync(default);
        backend.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<byte[]?>(new IOException("offline")));
        Assert.False(await layer.CheckAvailableAsync(default));
        backend.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<byte[]?>(null));
        oldValidation.SetResult(TimeSpan.FromDays(1));
        _ = await oldRead;
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await layer.CheckAvailableAsync(default));
        Assert.Equal(3, validations);
        Assert.True(layer.HasIncompatibleScope);
    }

    [Fact]
    public async Task SerializationPastDeadline_SkipsExpiredWriteWithoutOpeningBreaker()
    {
        var clock = new FakeTimeProvider();
        var backend = new AtomicTestCache();
        await using var layer = new DistributedCacheLayer(backend, new SlowSerializer(clock),
            new DistributedCacheOptions(), timeProvider: clock);
        await layer.WriteAsync("expired", 42, null, layer.Now, TimeSpan.FromSeconds(1));
        Assert.True(layer.IsAvailable);
        Assert.Null(backend.Get("methodcache:e6:s1:expired"));
    }

    [Fact]
    public async Task ReplacementExpiringDuringSerialization_RemovesEarlierDistributedValue()
    {
        var clock = new FakeTimeProvider();
        var backend = new AtomicTestCache(clock);
        await using var writer = new DistributedCacheLayer(backend, new JsonCacheSerializer(), new DistributedCacheOptions(), timeProvider: clock);
        await writer.WriteAsync("replace", 1, null, writer.Now, TimeSpan.FromMinutes(10), replaces: true);
        await using var slow = new DistributedCacheLayer(backend, new SlowSerializer(clock), new DistributedCacheOptions(), timeProvider: clock);
        await slow.WriteAsync("replace", 2, null, slow.Now, TimeSpan.FromSeconds(1), replaces: true);
        Assert.False((await writer.TryGetAsync<int>("replace")).Found);
        Assert.True(slow.IsAvailable);
    }

    private sealed class SlowSerializer(FakeTimeProvider clock) : ISerializer
    {
        private readonly JsonCacheSerializer _inner = new();
        public string ContentType => _inner.ContentType;
        public void Serialize<T>(T value, System.Buffers.IBufferWriter<byte> destination)
        {
            _inner.Serialize(value, destination);
            clock.Advance(TimeSpan.FromSeconds(2));
        }
        public T? Deserialize<T>(System.Buffers.ReadOnlySequence<byte> source) => _inner.Deserialize<T>(source);
    }

}
