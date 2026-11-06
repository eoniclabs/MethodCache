using Microsoft.Extensions.Time.Testing;

namespace MethodCache.Core.Tests.FaultInjection;

/// <summary>Signals when a batch's timeout timers are registered, before the test advances fake time.</summary>
internal sealed class TimerRegistrationTimeProvider(int expectedTimers) : FakeTimeProvider
{
    private readonly TaskCompletionSource _registered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _timers;
    private int _epoch;

    public Task TimersRegistered => _registered.Task;

    /// <summary>
    /// Counts only timers created from now on. A timer of <c>Task.WaitAsync</c> is disposed only after the continuations
    /// of the task it waits for have run, so the timer of an operation the test holds would still count while what
    /// follows that operation registers its own.
    /// </summary>
    public void CountOnlyNewTimers()
    {
        Interlocked.Increment(ref _epoch);
        Interlocked.Exchange(ref _timers, 0);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        var epoch = Volatile.Read(ref _epoch);
        if (Interlocked.Increment(ref _timers) == expectedTimers)
            _registered.TrySetResult();
        return new TrackedTimer(timer, () =>
        {
            if (Volatile.Read(ref _epoch) == epoch) Interlocked.Decrement(ref _timers);
        });
    }

    private sealed class TrackedTimer(ITimer inner, Action removed) : ITimer
    {
        private int _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

        public void Dispose()
        {
            inner.Dispose();
            Remove();
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            Remove();
        }

        private void Remove()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                removed();
        }
    }
}
