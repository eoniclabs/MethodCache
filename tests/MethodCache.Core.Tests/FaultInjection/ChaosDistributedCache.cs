using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>The distributed cache operations a fault can be injected into; each covers the sync and async call.</summary>
[Flags]
public enum ChaosOperations
{
    None = 0,
    Get = 1,
    Set = 2,
    Remove = 4,
    Refresh = 8,
    All = Get | Set | Remove | Refresh,
}

/// <summary>What a faulted call does.</summary>
public enum ChaosFaultKind
{
    /// <summary>Throws <see cref="ChaosException"/> without reaching the inner cache.</summary>
    Throw,

    /// <summary>Never answers until its token is cancelled, then throws <see cref="OperationCanceledException"/>.</summary>
    HangUntilCancelled,

    /// <summary>
    /// Ignores its token and waits until <see cref="ChaosDistributedCache.ReleaseHeld"/>, then reaches the inner cache
    /// anyway: a write that lands after the caller gave up on it.
    /// </summary>
    HangIgnoringToken,

    /// <summary>Waits <see cref="ChaosFault.Delay"/> on the cache's <see cref="TimeProvider"/>, honouring the token, then answers.</summary>
    Delay,

    /// <summary>
    /// Reaches the inner cache at once but answers after <see cref="ChaosFault.Delay"/>: a read returns what was
    /// stored when it was made, however much changed while the answer travelled.
    /// </summary>
    DelayResponse,

    /// <summary>Cuts the payload to <see cref="ChaosFault.KeepBytes"/> bytes (default: half): on Get the bytes returned, on Set the bytes stored.</summary>
    Truncate,

    /// <summary>Applies <see cref="ChaosFault.Corrupt"/> to the payload (default: every byte inverted), on Get or Set.</summary>
    Corrupt,
}

/// <summary>
/// A fault: what happens (<see cref="Kind"/>), to which calls (<see cref="EveryNth"/> of them, whose key passes
/// <see cref="Keys"/>) and how often (<see cref="Times"/>, unlimited when null).
/// </summary>
public sealed record ChaosFault(ChaosFaultKind Kind)
{
    public TimeSpan Delay { get; init; }

    /// <summary>1 faults every matching call, 3 faults the 3rd, 6th, 9th, ...</summary>
    public int EveryNth { get; init; } = 1;

    public Func<string, bool>? Keys { get; init; }

    public int? Times { get; init; }

    public int? KeepBytes { get; init; }

    public Func<byte[], byte[]>? Corrupt { get; init; }

    public static ChaosFault Throw() => new(ChaosFaultKind.Throw);

    public static ChaosFault HangUntilCancelled() => new(ChaosFaultKind.HangUntilCancelled);

    public static ChaosFault HangIgnoringToken() => new(ChaosFaultKind.HangIgnoringToken);

    public static ChaosFault Slow(TimeSpan delay) => new(ChaosFaultKind.Delay) { Delay = delay };

    public static ChaosFault SlowResponse(TimeSpan delay) => new(ChaosFaultKind.DelayResponse) { Delay = delay };

    public static ChaosFault Intermittent(int everyNth) => new(ChaosFaultKind.Throw) { EveryNth = everyNth };

    public static ChaosFault Truncated(int? keepBytes = null) => new(ChaosFaultKind.Truncate) { KeepBytes = keepBytes };

    public static ChaosFault Corrupted(Func<byte[], byte[]>? corrupt = null) => new(ChaosFaultKind.Corrupt) { Corrupt = corrupt };
}

public sealed class ChaosException(string message) : Exception(message);

/// <summary>
/// An <see cref="IDistributedCache"/> that passes calls to an inner cache (a <see cref="MemoryDistributedCache"/> by
/// default) and injects faults into the operations chosen with <see cref="Inject"/>: errors, hangs that honour or
/// ignore cancellation, delays on the injected <see cref="TimeProvider"/>, intermittent failures and damaged payloads.
/// Several nodes can share one instance, or each wrap the same inner cache with their own faults.
/// </summary>
public sealed class ChaosDistributedCache : MethodCache.Extensibility.IAtomicInvalidationStore
{
    private readonly object _lock = new();
    private readonly IDistributedCache _inner;
    private readonly TimeProvider _time;
    private readonly Dictionary<ChaosOperations, Rule> _rules = new();
    private TaskCompletionSource _release = NewGate();
    private readonly TaskCompletionSource _blockStarted = NewGate();
    private readonly TaskCompletionSource _blockCompleted = NewGate();
    private int _faultsStarted;
    private int _blocked;
    private int _held;
    private int _landedLate;

    public ChaosDistributedCache(IDistributedCache? inner = null, TimeProvider? time = null)
    {
        _inner = inner ?? new MethodCache.TestSupport.AtomicTestCache(time);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The cache the calls reach when they are not faulted.</summary>
    public IDistributedCache Inner => _inner;

    /// <summary>Calls that reached this cache, faulted or not.</summary>
    public int Calls { get; private set; }

    /// <summary>Calls a fault was applied to so far.</summary>
    public int FaultsStarted => Volatile.Read(ref _faultsStarted);

    /// <summary>Calls waiting right now in a hang or a delay.</summary>
    public int Blocked => Volatile.Read(ref _blocked);

    public Task FirstBlockStarted => _blockStarted.Task;

    public Task FirstBlockCompleted => _blockCompleted.Task;

    /// <summary>Calls waiting right now in <see cref="ChaosFaultKind.HangIgnoringToken"/>.</summary>
    public int Held => Volatile.Read(ref _held);

    /// <summary>Calls that reached the inner cache after <see cref="ReleaseHeld"/> let them go.</summary>
    public int LandedLate => Volatile.Read(ref _landedLate);

    /// <summary>Faults the given operations from now on, replacing what was injected into them before.</summary>
    public ChaosDistributedCache Inject(ChaosOperations operations, ChaosFault fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (fault.EveryNth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fault), "EveryNth must be at least 1.");
        }

        lock (_lock)
        {
            foreach (var operation in Single(operations))
            {
                _rules[operation] = new Rule(fault);
            }
        }

        return this;
    }

    /// <summary>Stops injecting faults; calls already hanging keep hanging until released or cancelled.</summary>
    public void Heal(ChaosOperations operations = ChaosOperations.All)
    {
        lock (_lock)
        {
            foreach (var operation in Single(operations))
            {
                _rules.Remove(operation);
            }
        }
    }

    /// <summary>Lets every call held by <see cref="ChaosFaultKind.HangIgnoringToken"/> go on to the inner cache.</summary>
    public void ReleaseHeld()
    {
        TaskCompletionSource release;
        lock (_lock)
        {
            release = _release;
            _release = NewGate();
        }

        release.SetResult();
    }

    public async Task WriteInvalidationStampAsync(string key, long timestamp, TimeSpan retention, CancellationToken cancellationToken = default)
    {
        var fault = await BeforeAsync(ChaosOperations.Set, key, cancellationToken).ConfigureAwait(false);
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, timestamp);
        bytes = Damage(fault.Fault, bytes)!;
        var token = fault.Landed ? CancellationToken.None : cancellationToken;
        if (bytes.Length == 8)
            await ((MethodCache.Extensibility.IAtomicInvalidationStore)_inner).WriteInvalidationStampAsync(key, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes), retention, token).ConfigureAwait(false);
        else
            await _inner.SetAsync(key, bytes, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = retention }, token).ConfigureAwait(false);
        CountLanding(fault.Landed);
        await AfterAsync(fault.Fault, cancellationToken).ConfigureAwait(false);
    }

    // ---------- async ----------

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var fault = await BeforeAsync(ChaosOperations.Get, key, token).ConfigureAwait(false);
        var bytes = await _inner.GetAsync(key, fault.Landed ? CancellationToken.None : token).ConfigureAwait(false);
        CountLanding(fault.Landed);
        await AfterAsync(fault.Fault, token).ConfigureAwait(false);
        return Damage(fault.Fault, bytes);
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        var fault = await BeforeAsync(ChaosOperations.Set, key, token).ConfigureAwait(false);
        await _inner.SetAsync(key, Damage(fault.Fault, value)!, options, fault.Landed ? CancellationToken.None : token).ConfigureAwait(false);
        CountLanding(fault.Landed);
        await AfterAsync(fault.Fault, token).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        var fault = await BeforeAsync(ChaosOperations.Remove, key, token).ConfigureAwait(false);
        await _inner.RemoveAsync(key, fault.Landed ? CancellationToken.None : token).ConfigureAwait(false);
        CountLanding(fault.Landed);
        await AfterAsync(fault.Fault, token).ConfigureAwait(false);
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        var fault = await BeforeAsync(ChaosOperations.Refresh, key, token).ConfigureAwait(false);
        await _inner.RefreshAsync(key, fault.Landed ? CancellationToken.None : token).ConfigureAwait(false);
        CountLanding(fault.Landed);
        await AfterAsync(fault.Fault, token).ConfigureAwait(false);
    }

    // ---------- sync: a hang blocks the calling thread, as a blocking client would ----------

    public byte[]? Get(string key)
    {
        var fault = BeforeAsync(ChaosOperations.Get, key, CancellationToken.None).GetAwaiter().GetResult();
        return Damage(fault.Fault, _inner.Get(key));
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        var fault = BeforeAsync(ChaosOperations.Set, key, CancellationToken.None).GetAwaiter().GetResult();
        _inner.Set(key, Damage(fault.Fault, value)!, options);
    }

    public void Remove(string key)
    {
        BeforeAsync(ChaosOperations.Remove, key, CancellationToken.None).GetAwaiter().GetResult();
        _inner.Remove(key);
    }

    public void Refresh(string key)
    {
        BeforeAsync(ChaosOperations.Refresh, key, CancellationToken.None).GetAwaiter().GetResult();
        _inner.Refresh(key);
    }

    // ---------- faults ----------

    /// <summary>Applies the operation's fault, if this call gets one; returns it when it changes the payload.</summary>
    private async Task<(ChaosFault? Fault, bool Landed)> BeforeAsync(ChaosOperations operation, string key, CancellationToken token)
    {
        ChaosFault? fault;
        TaskCompletionSource release;
        lock (_lock)
        {
            Calls++;
            fault = _rules.TryGetValue(operation, out var rule) ? rule.Next(key) : null;
            release = _release;
        }

        if (fault == null)
        {
            return (null, false);
        }

        Interlocked.Increment(ref _faultsStarted);
        switch (fault.Kind)
        {
            case ChaosFaultKind.Throw:
                throw new ChaosException($"Injected failure: {operation} {key}");

            case ChaosFaultKind.HangUntilCancelled:
                // Created before the call counts as blocked, so a test that sees it blocked can advance the clock
                var hang = Task.Delay(Timeout.InfiniteTimeSpan, _time, token);
                await Block(hang).ConfigureAwait(false);
                throw new InvalidOperationException("Unreachable: an infinite delay ended without cancellation.");

            case ChaosFaultKind.HangIgnoringToken:
                Interlocked.Increment(ref _held);
                try
                {
                    await Block(release.Task).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _held);
                }

                return (null, true);

            case ChaosFaultKind.Delay:
                await Block(Task.Delay(fault.Delay, _time, token)).ConfigureAwait(false);
                return (null, false);

            default:
                return (fault, false);
        }
    }

    private async Task AfterAsync(ChaosFault? fault, CancellationToken token)
    {
        if (fault?.Kind == ChaosFaultKind.DelayResponse)
        {
            await Block(Task.Delay(fault.Delay, _time, token)).ConfigureAwait(false);
        }
    }

    private void CountLanding(bool landed)
    {
        if (landed)
        {
            Interlocked.Increment(ref _landedLate);
        }
    }

    private async Task Block(Task wait)
    {
        Interlocked.Increment(ref _blocked);
        _blockStarted.TrySetResult();
        try
        {
            await wait.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _blocked);
            _blockCompleted.TrySetResult();
        }
    }

    private static byte[]? Damage(ChaosFault? fault, byte[]? bytes)
    {
        if (fault == null || bytes == null)
        {
            return bytes;
        }

        return fault.Kind switch
        {
            ChaosFaultKind.Truncate => bytes.AsSpan(0, Math.Min(bytes.Length, fault.KeepBytes ?? bytes.Length / 2)).ToArray(),
            ChaosFaultKind.Corrupt => fault.Corrupt?.Invoke(bytes.ToArray()) ?? bytes.Select(static b => (byte)~b).ToArray(),
            _ => bytes,
        };
    }

    private static IEnumerable<ChaosOperations> Single(ChaosOperations operations) =>
        new[] { ChaosOperations.Get, ChaosOperations.Set, ChaosOperations.Remove, ChaosOperations.Refresh }
            .Where(operation => operations.HasFlag(operation));

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Rule(ChaosFault fault)
    {
        private int _matching;
        private int _applied;

        public ChaosFault? Next(string key)
        {
            if (fault.Keys != null && !fault.Keys(key))
            {
                return null;
            }

            if (++_matching % fault.EveryNth != 0 || (fault.Times is { } times && _applied >= times))
            {
                return null;
            }

            _applied++;
            return fault;
        }
    }
}
