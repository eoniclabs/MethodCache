using MethodCache.Benchmarks.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MethodCache.Benchmarks.Tests;

public class SerializationCleanupTests
{
    [Fact]
    public void GlobalCleanup_DisposesEveryKeyGeneratorProvider()
    {
        var clock = new TrackingClock();
        var benchmark = new ProbeBenchmark(clock);
        benchmark.GlobalSetup();
        Assert.Equal(3, clock.ActiveTimers);

        benchmark.GlobalCleanup();

        Assert.Equal(0, clock.ActiveTimers);
        benchmark.GlobalCleanup();
        Assert.Equal(0, clock.ActiveTimers);
    }

    private sealed class ProbeBenchmark(TimeProvider clock) : SerializationBenchmarks
    {
        protected override void ConfigureServices(IServiceCollection services)
        {
            base.ConfigureServices(services);
            services.AddSingleton(clock);
        }
    }

    // No wall-clock timers: these timers only record their lifetime.
    private sealed class TrackingClock : TimeProvider
    {
        public int ActiveTimers { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ActiveTimers++;
            return new Timer(this);
        }
        private sealed class Timer(TrackingClock owner) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                owner.ActiveTimers--;
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
