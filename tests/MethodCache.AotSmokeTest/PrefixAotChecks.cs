using System.Diagnostics.Metrics;
using MethodCache;
using Microsoft.Extensions.DependencyInjection;
using LongSource = MethodCache.AotSmokeTest.PrefixCases.WithAnIntentionallyLongNamespace.ToExerciseGeneratedKeyConstructionBeyondTheInlineStackBuffer.AndFullCacheHitsAndMissesUnderNativeAot.WithBothIntAndGuidArguments.AndAllReturnShapes.WithListenersEnabledAndDisabled.ILongPrefixSource;
using ShortSource = MethodCache.AotSmokeTest.PrefixCases.IShortPrefixSource;

namespace MethodCache.AotSmokeTest.PrefixCases
{
    internal static class PrefixAotChecks
    {
        internal static async Task RunAsync()
        {
            var first = new Guid("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
            var second = new Guid("7f9619ff-8b86-d011-b42d-00cf4fc964ff");
            foreach (var listening in new[] { false, true })
            {
                long requests = 0;
                using var listener = new MeterListener
                {
                    InstrumentPublished = static (instrument, self) =>
                    {
                        if (instrument.Meter.Name.StartsWith("MethodCache", StringComparison.Ordinal))
                            self.EnableMeasurementEvents(instrument);
                    }
                };
                listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
                {
                    if (instrument.Name == "methodcache.requests") Interlocked.Add(ref requests, value);
                });
                if (listening) listener.Start();

                var origin = new PrefixSource();
                var services = new ServiceCollection();
                services.AddSingleton<ShortSource>(origin);
                services.AddSingleton<LongSource>(origin);
                services.AddMethodCache(b => b.FromAssemblyOf<ShortSource>());
                await using var provider = services.BuildServiceProvider();
                var shortSource = provider.GetRequiredService<ShortSource>();
                var longSource = provider.GetRequiredService<LongSource>();

                foreach (var longPrefix in new[] { false, true })
                {
                    var keys = provider.GetRequiredService<IMethodCacheKeys>();
                    var key = longPrefix ? keys.For<LongSource>(source => source.Read(first)) : keys.For<ShortSource>(source => source.Read(first));
                    if ((key.Length > 256) != longPrefix)
                        throw new InvalidOperationException("Native prefix fixture did not cross the intended key boundary.");

                    await CheckAsync(id => new ValueTask<int>(longPrefix ? longSource.Read(id) : shortSource.Read(id)), first, second, origin);
                    await CheckAsync(id => new ValueTask<int>(longPrefix ? longSource.ReadAsync(id) : shortSource.ReadAsync(id)), first, second, origin);
                    await CheckAsync(id => longPrefix ? longSource.ReadValueAsync(id) : shortSource.ReadValueAsync(id), first, second, origin);
                    await CheckAsync(id => new ValueTask<int>(longPrefix ? longSource.ReadInt(id) : shortSource.ReadInt(id)), 42, 43, origin);
                    await CheckAsync(id => new ValueTask<int>(longPrefix ? longSource.ReadIntAsync(id) : shortSource.ReadIntAsync(id)), 42, 43, origin);
                    await CheckAsync(id => longPrefix ? longSource.ReadIntValueAsync(id) : shortSource.ReadIntValueAsync(id), 42, 43, origin);
                }

                if (requests != (listening ? 48 : 0))
                    throw new InvalidOperationException($"Native prefix listener recorded {requests} requests in listening={listening}.");
            }

            Console.WriteLine("Native prefix hit/miss checks passed: short/long, int/Guid, sync/Task/ValueTask, listeners off/on.");
        }

        private static async Task CheckAsync<T>(Func<T, ValueTask<int>> read, T first, T second, PrefixSource origin)
        {
            var before = origin.Loads;
            if (await read(first) != before + 1 || await read(first) != before + 1 ||
                await read(second) != before + 2 || await read(second) != before + 2 || origin.Loads != before + 2)
                throw new InvalidOperationException("Native generated prefix path did not preserve values, misses and resident hits.");
        }
    }

    public interface IShortPrefixSource
    {
        [Cache] int Read(Guid id);
        [Cache] Task<int> ReadAsync(Guid id);
        [Cache] ValueTask<int> ReadValueAsync(Guid id);
        [Cache] int ReadInt(int id);
        [Cache] Task<int> ReadIntAsync(int id);
        [Cache] ValueTask<int> ReadIntValueAsync(int id);
    }

    public sealed class PrefixSource : ShortSource, LongSource
    {
        private int _loads;
        internal int Loads => Volatile.Read(ref _loads);
        private int Loaded() => Interlocked.Increment(ref _loads);
        public int Read(Guid id) => Loaded();
        public Task<int> ReadAsync(Guid id) => Task.FromResult(Loaded());
        public ValueTask<int> ReadValueAsync(Guid id) => ValueTask.FromResult(Loaded());
        public int ReadInt(int id) => Loaded();
        public Task<int> ReadIntAsync(int id) => Task.FromResult(Loaded());
        public ValueTask<int> ReadIntValueAsync(int id) => ValueTask.FromResult(Loaded());
    }
}

namespace MethodCache.AotSmokeTest.PrefixCases.WithAnIntentionallyLongNamespace.ToExerciseGeneratedKeyConstructionBeyondTheInlineStackBuffer.AndFullCacheHitsAndMissesUnderNativeAot.WithBothIntAndGuidArguments.AndAllReturnShapes.WithListenersEnabledAndDisabled
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
