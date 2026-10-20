using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using ShortSource = MethodCache.Benchmarks.Microbenchmarks.GeneratedPrefixScenarios.IShortPrefixSource;
using LongSource = MethodCache.Benchmarks.Microbenchmarks.GeneratedPrefixScenarios.WithAnIntentionallyLongNamespace.ForTestingTheGeneratedKeyPrefixAndTheGeneratorSelectedStackBuffer.WithStableNamesAcrossAllComparedRevisions.AndAdditionalSegmentsToCrossTheShortKeyBoundary.ILongPrefixSource;

namespace MethodCache.Benchmarks.Microbenchmarks
{
    public enum PrefixArgumentKind { Int, Guid }

    // Calls the generated decorators through their service interfaces. The neighboring facade benchmark
    // deliberately controls buffer sizes by hand and measures a different part of the path.
    [MemoryDiagnoser]
    public class GeneratedPrefixHitPathBenchmarks
    {
        private static readonly Guid Id = new("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
        private const int IntId = -1; // Distinct from every positive miss ID.
        private ServiceProvider _provider = null!;
        private ShortSource _short = null!;
        private LongSource _long = null!;
        private MeterListener? _listener;
        private int _next;

        [Params(false, true)] public bool LongPrefix { get; set; }
        [Params(false, true)] public bool Listening { get; set; }
        [Params(PrefixArgumentKind.Int, PrefixArgumentKind.Guid)] public PrefixArgumentKind ArgumentKind { get; set; } = PrefixArgumentKind.Guid;
        public bool SelectPerformanceCores { get; set; } = true;
        internal GeneratedPrefixSource OriginService => _provider.GetRequiredService<GeneratedPrefixSource>();
        internal int OriginLoads => OriginService.OriginLoads;
        internal int KeyLength => Key(_provider.GetRequiredService<IMethodCacheKeys>()).Length;

        private string Key(IMethodCacheKeys keys) => (LongPrefix, ArgumentKind) switch
        {
            (true, PrefixArgumentKind.Guid) => keys.For<LongSource>(source => source.Read(Id)),
            (true, _) => keys.For<LongSource>(source => source.ReadInt(IntId)),
            (false, PrefixArgumentKind.Guid) => keys.For<ShortSource>(source => source.Read(Id)),
            _ => keys.For<ShortSource>(source => source.ReadInt(IntId)),
        };

        [GlobalSetup]
        public void Setup()
        {
            if (SelectPerformanceCores) PrefixBenchmarkSetup.PreferPerformanceCores();
            if (Listening)
            {
                _listener = new MeterListener
                {
                    InstrumentPublished = static (instrument, listener) =>
                    {
                        if (instrument.Meter.Name == "MethodCache") listener.EnableMeasurementEvents(instrument);
                    }
                };
                _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
                _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
                _listener.Start();
            }
            var services = new ServiceCollection();
            services.AddSingleton<GeneratedPrefixSource>();
            services.AddSingleton<ShortSource>(provider => provider.GetRequiredService<GeneratedPrefixSource>());
            services.AddSingleton<LongSource>(provider => provider.GetRequiredService<GeneratedPrefixSource>());
            services.Configure<MethodCacheMemoryOptions>(options => options.MaxItems = 1024);
            services.AddMethodCache();
            _provider = services.BuildServiceProvider();
            _short = _provider.GetRequiredService<ShortSource>();
            _long = _provider.GetRequiredService<LongSource>();
            if ((KeyLength > 256) != LongPrefix)
                throw new InvalidOperationException("Generated key did not exercise the declared prefix length.");
            _ = SyncHit();
            _ = TaskHit().GetAwaiter().GetResult();
            _ = ValueTaskHit().GetAwaiter().GetResult();
            if (OriginLoads != 3) throw new InvalidOperationException("Generated prefix setup did not populate each return shape.");
            VerifyResidentHits();
            VerifyUniqueMisses();
        }

        internal void VerifyResidentHits()
        {
            var before = OriginLoads;
            for (var i = 0; i < 100; i++)
                if (SyncHit() != 42 || TaskHit().GetAwaiter().GetResult() != 42 || ValueTaskHit().GetAwaiter().GetResult() != 42)
                    throw new InvalidOperationException("Generated prefix hit returned an incorrect value.");
            if (OriginLoads != before) throw new InvalidOperationException("Generated prefix hit bypassed the cache.");
        }

        internal void VerifyUniqueMisses()
        {
            var before = OriginLoads;
            for (var i = 0; i < 10; i++)
            {
                if (SyncMiss() != 42 || TaskMiss().GetAwaiter().GetResult() != 42 || ValueTaskMiss().GetAwaiter().GetResult() != 42)
                    throw new InvalidOperationException("Generated prefix miss returned an incorrect value.");
                if (OriginLoads != before + 3 * (i + 1))
                    throw new InvalidOperationException("Generated prefix miss reused an existing key.");
            }
        }

        [Benchmark]
        public int SyncHit() => ArgumentKind == PrefixArgumentKind.Guid
            ? LongPrefix ? _long.Read(Id) : _short.Read(Id)
            : LongPrefix ? _long.ReadInt(IntId) : _short.ReadInt(IntId);
        [Benchmark]
        public Task<int> TaskHit() => ArgumentKind == PrefixArgumentKind.Guid
            ? LongPrefix ? _long.ReadAsync(Id) : _short.ReadAsync(Id)
            : LongPrefix ? _long.ReadIntAsync(IntId) : _short.ReadIntAsync(IntId);
        [Benchmark]
        public ValueTask<int> ValueTaskHit() => ArgumentKind == PrefixArgumentKind.Guid
            ? LongPrefix ? _long.ReadValueAsync(Id) : _short.ReadValueAsync(Id)
            : LongPrefix ? _long.ReadIntValueAsync(IntId) : _short.ReadIntValueAsync(IntId);
        [Benchmark]
        public int SyncMiss() => ArgumentKind == PrefixArgumentKind.Guid
            ? LongPrefix ? _long.Read(NextId()) : _short.Read(NextId())
            : LongPrefix ? _long.ReadInt(++_next) : _short.ReadInt(++_next);
        [Benchmark]
        public Task<int> TaskMiss() => ArgumentKind == PrefixArgumentKind.Guid
            ? LongPrefix ? _long.ReadAsync(NextId()) : _short.ReadAsync(NextId())
            : LongPrefix ? _long.ReadIntAsync(++_next) : _short.ReadIntAsync(++_next);
        [Benchmark]
        public ValueTask<int> ValueTaskMiss() => ArgumentKind == PrefixArgumentKind.Guid
            ? LongPrefix ? _long.ReadValueAsync(NextId()) : _short.ReadValueAsync(NextId())
            : LongPrefix ? _long.ReadIntValueAsync(++_next) : _short.ReadIntValueAsync(++_next);
        private Guid NextId() => new(++_next, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        [GlobalCleanup]
        public void Cleanup() { _provider.Dispose(); _listener?.Dispose(); }
    }

    public sealed class GeneratedPrefixSource : ShortSource, LongSource
    {
        private int _loads;
        internal int OriginLoads => Volatile.Read(ref _loads);
        private int Loaded() { Interlocked.Increment(ref _loads); return 42; }
        public int Read(Guid id) => Loaded();
        public Task<int> ReadAsync(Guid id) => Task.FromResult(Loaded());
        public ValueTask<int> ReadValueAsync(Guid id) => ValueTask.FromResult(Loaded());
        public int ReadInt(int id) => Loaded();
        public Task<int> ReadIntAsync(int id) => Task.FromResult(Loaded());
        public ValueTask<int> ReadIntValueAsync(int id) => ValueTask.FromResult(Loaded());
    }
}

namespace MethodCache.Benchmarks.Microbenchmarks.GeneratedPrefixScenarios
{
    public interface IShortPrefixSource
    {
        [Cache] int Read(Guid id);
        [Cache] Task<int> ReadAsync(Guid id);
        [Cache] ValueTask<int> ReadValueAsync(Guid id);
        [Cache] int ReadInt(int id);
        [Cache] Task<int> ReadIntAsync(int id);
        [Cache] ValueTask<int> ReadIntValueAsync(int id);
    }
}

// The namespace is actual generator input, rather than padding a manually bound method descriptor.
namespace MethodCache.Benchmarks.Microbenchmarks.GeneratedPrefixScenarios.WithAnIntentionallyLongNamespace.ForTestingTheGeneratedKeyPrefixAndTheGeneratorSelectedStackBuffer.WithStableNamesAcrossAllComparedRevisions.AndAdditionalSegmentsToCrossTheShortKeyBoundary
{
    public interface ILongPrefixSource
    {
        [Cache] int Read(Guid id);
        [Cache] Task<int> ReadAsync(Guid id);
        [Cache] ValueTask<int> ReadValueAsync(Guid id);
        [Cache] int ReadInt(int id);
        [Cache] Task<int> ReadIntAsync(int id);
        [Cache] ValueTask<int> ReadIntValueAsync(int id);
    }
}
