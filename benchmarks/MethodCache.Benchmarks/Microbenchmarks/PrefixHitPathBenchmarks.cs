using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using MethodCache.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Microbenchmarks;

internal static class PrefixBenchmarkSetup
{
    internal static void PreferPerformanceCores()
    {
        if (OperatingSystem.IsMacOS() && SetQos(0x19, 0) != 0)
            throw new InvalidOperationException("Could not select benchmark thread QoS.");
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static extern int SetQos(uint quality, int priority);
}

// This source uses only generated contract 1, so the identical workload can be compiled against its pre-hash base.
[MemoryDiagnoser]
public class PrefixHitPathBenchmarks
{
    private ServiceProvider _provider = null!;
    private CachedMethod _method = null!;
    private MeterListener? _listener;
    internal int OriginLoads { get; private set; }
    [Params(4, 256)] public int MethodNameLength { get; set; }
    [Params(128, 512)] public int BufferCharacters { get; set; }
    [Params(false, true)] public bool Listening { get; set; }
    public bool SelectPerformanceCores { get; set; } = true;

    [GlobalSetup]
    public void Setup()
    {
        if (SelectPerformanceCores) PrefixBenchmarkSetup.PreferPerformanceCores();
        var services = new ServiceCollection();
        services.AddSingleton<IPrefixSource, PrefixSource>();
        services.AddMethodCache();
        _provider = services.BuildServiceProvider();
        if (Listening)
        {
            _listener = new MeterListener();
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "MethodCache") listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            _listener.Start();
        }
        _method = _provider.GetRequiredService<GeneratedServices>().Method(typeof(IPrefixSource).FullName + ".Read",
            typeof(IPrefixSource), new string('M', MethodNameLength), "PrefixBench.Read");
        _ = SyncHit();
        _ = TaskHit().GetAwaiter().GetResult();
        _ = ValueTaskHit().GetAwaiter().GetResult();
    }

    [Benchmark]
    public int SyncHit()
    {
        var call = _method.Capture();
        var key = call.StartKey(stackalloc char[BufferCharacters]);
        key.AppendPart(42);
        if (call.TryGetValue<int>(ref key, out var value)) return value;
        return call.Load(key.ToString(), this, static (_, state) => { state.OriginLoads++; return 42; }, [], CancellationToken.None);
    }

    [Benchmark]
    public Task<int> TaskHit()
    {
        var call = _method.Capture();
        var key = call.StartKey(stackalloc char[BufferCharacters]);
        key.AppendPart(42);
        if (call.TryGetTask<int>(ref key, out var value)) return value;
        return call.LoadAsync(key.ToString(), this, static (_, state) => { state.OriginLoads++; return Task.FromResult(42); }, [], CancellationToken.None);
    }

    [Benchmark]
    public ValueTask<int> ValueTaskHit()
    {
        var call = _method.Capture();
        var key = call.StartKey(stackalloc char[BufferCharacters]);
        key.AppendPart(42);
        if (call.TryGetValueTask<int>(ref key, out var value)) return value;
        return call.LoadValueAsync(key.ToString(), this, static (_, state) => { state.OriginLoads++; return ValueTask.FromResult(42); }, [], CancellationToken.None);
    }

    [Benchmark]
    public int KeyBuild()
    {
        var call = _method.Capture();
        var key = call.StartKey(stackalloc char[BufferCharacters]);
        key.AppendPart(42);
        return key.AsSpan().Length;
    }

    [GlobalCleanup]
    public void Cleanup() { _listener?.Dispose(); _provider.Dispose(); }
}

public interface IPrefixSource { [Cache] int Read(int id); }
public sealed class PrefixSource : IPrefixSource { public int Read(int id) => id; }
