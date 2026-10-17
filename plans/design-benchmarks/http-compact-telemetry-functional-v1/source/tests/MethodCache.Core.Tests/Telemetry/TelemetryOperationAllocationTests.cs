using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using MethodCache.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace MethodCache.Core.Tests.Telemetry;

[Collection(nameof(Core.CpuIntensiveCollection))]
public class TelemetryOperationAllocationTests
{
    [Fact]
    public void EnabledLoadScopes_DoNotReserveEightInlineTagsPerOperation()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        using var telemetry = new CoreTelemetry(factory, MsOptions.Create(new MethodCacheTelemetryOptions()));
        Assert.False(telemetry.Source.HasListeners());
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, self) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, factory) && instrument.Name == "methodcache.load.duration")
                    self.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        listener.Start();
        _ = Measure(telemetry);
        var bytes = Measure(telemetry) / 1000d;
        Assert.True(bytes < 128, $"A two-tag load scope allocates {bytes:F1} B; unused tag capacity must stay on the recording stack.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(CoreTelemetry telemetry)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            using var operation = telemetry.BeginLoad("http", "probe");
            if (operation == null) throw new InvalidOperationException("The measured load histogram must have a listener.");
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
