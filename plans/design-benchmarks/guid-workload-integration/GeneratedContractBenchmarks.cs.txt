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
    [GlobalSetup]
    public void Setup()
    {
        // Prefer performance cores for this process under macOS; use the same setting on both revisions.
        if (OperatingSystem.IsMacOS() && SetQualityOfService(0x19, 0) != 0)
            throw new InvalidOperationException("Could not set benchmark thread quality of service.");
        var services = new ServiceCollection();
        services.AddSingleton<IContractService, ContractService>();
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
    public Task<int> Generic<T>(Guid id) => System.Threading.Tasks.Task.FromResult(42);
    public int Sync(Guid id) => 42;
    public Task<int> Task(Guid id) => System.Threading.Tasks.Task.FromResult(42);
    public Task<int> ByEmail(string email) => System.Threading.Tasks.Task.FromResult(email.Length);
    public ValueTask<int> ValueTask(Guid id) => new(42);
    public Task<int> Record(ContractQuery query) => System.Threading.Tasks.Task.FromResult(query.Page);
    public Task<int> Collection(Guid[] ids) => System.Threading.Tasks.Task.FromResult(ids.Length);
    public Task<int> Fallback(object value) => System.Threading.Tasks.Task.FromResult(42);
}
