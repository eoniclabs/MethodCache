using BenchmarkDotNet.Attributes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Comparison;

/// <summary>Identical workloads for comparing generated-contract revisions. Ids are GUIDs, the common key in applications.</summary>
[MemoryDiagnoser]
public class GeneratedContractBenchmarks
{
    private ServiceProvider _provider = null!;
    private IContractService _service = null!;
    private readonly ContractQuery _query = new("a:\\b\ud800", 42);
    private static readonly Guid Id = new("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    private readonly Guid[] _ids =
    [
        new("0b0f8c3e-3c5a-4f6e-9d1a-2f4b6c8d0e1f"),
        new("5a7c9e1b-2d4f-4a6c-8e0b-1c3d5e7f9a2b"),
        new("9e8d7c6b-5a49-4382-a716-0f1e2d3c4b5a"),
    ];
    private readonly object _fallback = new ContractQuery("a:\\b\ud800", 42);
    private int _next;
    public bool SelectPerformanceCores { get; set; } = true;
    internal ContractService OriginService => _provider.GetRequiredService<ContractService>();
    internal int OriginLoads => OriginService.OriginLoads;
    [GlobalSetup]
    public void Setup()
    {
        // Prefer performance cores for this process under macOS; use the same setting on both revisions.
        if (SelectPerformanceCores && OperatingSystem.IsMacOS())
        {
            if (GetThreadQos(CurrentThread(), out var quality, out _) != 0)
                throw new InvalidOperationException("Could not inspect benchmark thread QoS.");
            // POSIX scheduling opts a thread out of QoS permanently. In-process BDN
            // owns that priority; retain it instead of attempting a forbidden override.
            if (quality != 0 && SetQualityOfService(0x19, 0) != 0)
                throw new InvalidOperationException("Could not set benchmark thread quality of service.");
        }
        var services = new ServiceCollection();
        services.AddSingleton<ContractService>();
        services.AddSingleton<IContractService>(provider => provider.GetRequiredService<ContractService>());
        services.Configure<MethodCacheMemoryOptions>(options => options.MaxItems = 1024);
        services.AddMethodCache();
        _provider = services.BuildServiceProvider();
        _service = _provider.GetRequiredService<IContractService>();
        _ = SyncHit();
        _ = TaskHit().GetAwaiter().GetResult();
        _ = ValueTaskHit().GetAwaiter().GetResult();
        _ = RecordHit().GetAwaiter().GetResult();
        _ = CollectionHit().GetAwaiter().GetResult();
        _ = FallbackHit().GetAwaiter().GetResult();
        _ = GenericHit().GetAwaiter().GetResult();
        _ = StringHit().GetAwaiter().GetResult();
        if (OriginLoads != 8) throw new InvalidOperationException("Each declared hit must populate exactly once.");
        VerifyResidentHits();
    }

    internal void VerifyResidentHits()
    {
        var before = OriginLoads;
        for (var i = 0; i < 100; i++)
        {
            if (SyncHit() != 42 || GenericHit().GetAwaiter().GetResult() != 42 ||
                TaskHit().GetAwaiter().GetResult() != 42 || ValueTaskHit().GetAwaiter().GetResult() != 42 ||
                StringHit().GetAwaiter().GetResult() != 24 || RecordHit().GetAwaiter().GetResult() != 42 ||
                CollectionHit().GetAwaiter().GetResult() != 3 || FallbackHit().GetAwaiter().GetResult() != 42)
                throw new InvalidOperationException("Generated hit returned the wrong value.");
        }
        if (OriginLoads != before) throw new InvalidOperationException("Generated hit invoked the origin.");
    }

    internal void VerifyUniqueMisses()
    {
        var before = OriginLoads;
        for (var i = 0; i < 10; i++)
        {
            if (SyncMiss() != 42 || ValueTaskMiss().GetAwaiter().GetResult() != 42 ||
                TaskMiss().GetAwaiter().GetResult() != 42 || OriginLoads != before + 3 * (i + 1))
                throw new InvalidOperationException("Each generated miss must invoke the origin with a distinct key.");
        }
    }

    [Benchmark] public int SyncHit() => _service.Sync(Id);
    [Benchmark] public Task<int> GenericHit() => _service.Generic<int>(Id);
    [Benchmark] public Task<int> TaskHit() => _service.Task(Id);
    [Benchmark] public ValueTask<int> ValueTaskHit() => _service.ValueTask(Id);
    [Benchmark] public Task<int> StringHit() => _service.ByEmail("ada.lovelace@example.com");
    [Benchmark] public Task<int> RecordHit() => _service.Record(_query);
    [Benchmark] public Task<int> CollectionHit() => _service.Collection(_ids);
    [Benchmark] public Task<int> FallbackHit() => _service.Fallback(_fallback);
    [Benchmark] public int SyncMiss() => _service.Sync(NextMissId());
    [Benchmark] public ValueTask<int> ValueTaskMiss() => _service.ValueTask(NextMissId());
    [Benchmark] public Task<int> TaskMiss() => _service.Task(NextMissId());

    // A distinct GUID per call, without adding Guid.NewGuid's random-number cost to every miss
    private Guid NextMissId() => new(++_next, 0x4d43, 0x4000, 0x80, 0, 0, 0, 0, 0, 0, 0);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_self")]
    private static extern nint CurrentThread();

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_get_qos_class_np")]
    private static extern int GetThreadQos(nint thread, out uint quality, out int priority);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static extern int SetQualityOfService(uint quality, int relativePriority);

    [GlobalCleanup] public void Cleanup() => _provider.Dispose();
}

public sealed record ContractQuery(string Text, int Page);

public interface IContractService
{
    [Cache(Duration = "00:10:00")] Task<int> Generic<T>(Guid id);
    [Cache(Duration = "00:10:00")] int Sync(Guid id);
    [Cache(Duration = "00:10:00")] Task<int> ByEmail(string email);
    [Cache(Duration = "00:10:00")] Task<int> Task(Guid id);
    [Cache(Duration = "00:10:00")] ValueTask<int> ValueTask(Guid id);
    [Cache(Duration = "00:10:00")] Task<int> Record(ContractQuery query);
    [Cache(Duration = "00:10:00")] Task<int> Collection(Guid[] ids);
    [Cache(Duration = "00:10:00")] Task<int> Fallback(object value);
}

public sealed class ContractService : IContractService
{
    private int _originLoads;
    internal int OriginLoads => Volatile.Read(ref _originLoads);
    private int Loaded(int value) { Interlocked.Increment(ref _originLoads); return value; }
    public Task<int> Generic<T>(Guid id) => System.Threading.Tasks.Task.FromResult(Loaded(42));
    public int Sync(Guid id) => Loaded(42);
    public Task<int> Task(Guid id) => System.Threading.Tasks.Task.FromResult(Loaded(42));
    public Task<int> ByEmail(string email) => System.Threading.Tasks.Task.FromResult(Loaded(email.Length));
    public ValueTask<int> ValueTask(Guid id) => new(Loaded(42));
    public Task<int> Record(ContractQuery query) => System.Threading.Tasks.Task.FromResult(Loaded(query.Page));
    public Task<int> Collection(Guid[] ids) => System.Threading.Tasks.Task.FromResult(Loaded(ids.Length));
    public Task<int> Fallback(object value) => System.Threading.Tasks.Task.FromResult(Loaded(42));
}
