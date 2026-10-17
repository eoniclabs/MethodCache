using MethodCache.Benchmarks.Microbenchmarks;

namespace MethodCache.CostProfile;

internal static class ComponentGuards
{
    internal static async Task RunAsync()
    {
        if (!KeyHashLookupBenchmarks.UsesPrefixHash || !KeyHashLookupBenchmarks.UsesSipHash || !PrefixHitPathBenchmarks.UsesInPlaceKeyBuilder)
            throw new InvalidOperationException("Component build did not select the final SipHash/constructor paths.");
        foreach (var length in new[] { 4, 80, 256 })
        foreach (var argument in new[] { "Int", "Guid" })
        {
            var value = new KeyHashLookupBenchmarks { PrefixCharacters = length, ArgumentKind = argument, SelectPerformanceCores = false };
            value.Setup();
            if (value.ReadyStringLookup() != 42 || value.FullSpanLookup() != 42 || value.PrefixLookup() != 42)
                throw new InvalidOperationException("Component lookup failed.");
        }
        foreach (var length in new[] { 4, 256 })
        foreach (var buffer in new[] { 128, 512 })
        foreach (var listening in new[] { false, true })
        {
            var value = new PrefixHitPathBenchmarks
            { MethodNameLength = length, BufferCharacters = buffer, Listening = listening, SelectPerformanceCores = false };
            value.Setup();
            try
            {
                for (var i = 0; i < 100; i++)
                    if (value.SyncHit() != 42 || await value.TaskHit() != 42 || await value.ValueTaskHit() != 42)
                        throw new InvalidOperationException("Component facade did not return its resident value.");
                var keyLength = value.KeyBuild();
                if (value.OriginLoads != 1 || keyLength <= length || (length == 256 && buffer == 128 && keyLength <= buffer))
                    throw new InvalidOperationException("Component growth/origin guard failed.");
            }
            finally { value.Cleanup(); }
        }
        var reference = new MemoryCacheLookupReferenceBenchmarks { SelectPerformanceCores = false };
        reference.Setup();
        try
        {
            if (reference.ReadyKeySync() != 42 || reference.BuiltKeySync() != 42 ||
                await reference.ReadyKeyTask() != 42 || await reference.BuiltKeyTask() != 42)
                throw new InvalidOperationException("Memory-cache reference did not hit.");
        }
        finally { reference.Cleanup(); }
        Console.WriteLine("Verified lookup/hash capabilities, actual prefix/growth/listener paths and four distinct memory-cache references.");
    }
}
