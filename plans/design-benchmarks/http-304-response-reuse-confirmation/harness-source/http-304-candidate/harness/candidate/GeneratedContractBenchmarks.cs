using BenchmarkDotNet.Attributes;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace MethodCache.Benchmarks.Comparison;

/// <summary>Identical workloads for comparing generated-contract revisions.</summary>
[MemoryDiagnoser]
public class GeneratedContractBenchmarks
{
    private ServiceProvider _provider = null!;
    private IContractService _service = null!;
    private readonly ContractQuery _query = new("a:\\b\ud800", 42);
    private readonly int[] _ids = [1, 2, 3];
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
    }

    [Benchmark] public int SyncHit() => _service.Sync(42);
    [Benchmark] public Task<int> GenericHit() => _service.Generic<int>(42);
    [Benchmark] public Task<int> TaskHit() => _service.Task(42);
    [Benchmark] public ValueTask<int> ValueTaskHit() => _service.ValueTask(42);
    [Benchmark] public Task<int> RecordHit() => _service.Record(_query);
    [Benchmark] public Task<int> CollectionHit() => _service.Collection(_ids);
    [Benchmark] public Task<int> FallbackHit() => _service.Fallback(_fallback);
    [Benchmark] public int SyncMiss() => _service.Sync(++_next + 1000);
    [Benchmark] public ValueTask<int> ValueTaskMiss() => _service.ValueTask(++_next + 1000);
    [Benchmark] public Task<int> TaskMiss() => _service.Task(++_next + 1000);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
    private static extern int SetQualityOfService(uint quality, int relativePriority);

    [GlobalCleanup] public void Cleanup() => _provider.Dispose();
}

public sealed record ContractQuery(string Text, int Page);

public interface IContractService
{
    [Cache(Duration = "00:10:00")] Task<int> Generic<T>(int id);
    [Cache(Duration = "00:10:00")] int Sync(int id);
    [Cache(Duration = "00:10:00")] Task<int> Task(int id);
    [Cache(Duration = "00:10:00")] ValueTask<int> ValueTask(int id);
    [Cache(Duration = "00:10:00")] Task<int> Record(ContractQuery query);
    [Cache(Duration = "00:10:00")] Task<int> Collection(int[] ids);
    [Cache(Duration = "00:10:00")] Task<int> Fallback(object value);
}

public sealed class ContractService : IContractService
{
    public Task<int> Generic<T>(int id) => System.Threading.Tasks.Task.FromResult(id);
    public int Sync(int id) => id;
    public Task<int> Task(int id) => System.Threading.Tasks.Task.FromResult(id);
    public ValueTask<int> ValueTask(int id) => new(id);
    public Task<int> Record(ContractQuery query) => System.Threading.Tasks.Task.FromResult(query.Page);
    public Task<int> Collection(int[] ids) => System.Threading.Tasks.Task.FromResult(ids.Length);
    public Task<int> Fallback(object value) => System.Threading.Tasks.Task.FromResult(42);
}
