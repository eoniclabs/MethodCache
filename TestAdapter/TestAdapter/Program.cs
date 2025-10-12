using System;
using System.Diagnostics;
using MethodCache.Benchmarks.Comparison.Adapters;
using MethodCache.Benchmarks.Comparison;

Console.WriteLine("Testing MethodCache adapter variants...\n");

// Create adapters
var original = new MethodCacheAdapter();
var fixedSimple = new SimpleFixedMethodCacheAdapter();
var optimized = new ProperlyOptimizedMethodCacheAdapter();
var baseline = new MemoryCacheAdapter();

// Test data
var testKey = "test_key";
var testValue = new SamplePayload { Id = 1, Name = "Test", Data = new byte[1024] };

// Warm up
Console.WriteLine("Warming up caches...");
original.Set(testKey, testValue, TimeSpan.FromMinutes(1));
fixedSimple.Set(testKey, testValue, TimeSpan.FromMinutes(1));
optimized.Set(testKey, testValue, TimeSpan.FromMinutes(1));
baseline.Set(testKey, testValue, TimeSpan.FromMinutes(1));

// Wait for sets to complete
System.Threading.Thread.Sleep(100);

// Verify cache hits
Console.WriteLine("Verifying cache hits:");
Console.WriteLine($"Original:     {original.TryGet<SamplePayload>(testKey, out _)}");
Console.WriteLine($"FixedSimple:  {fixedSimple.TryGet<SamplePayload>(testKey, out _)}");
Console.WriteLine($"Optimized:    {optimized.TryGet<SamplePayload>(testKey, out _)}");
Console.WriteLine($"Baseline:     {baseline.TryGet<SamplePayload>(testKey, out _)}");
Console.WriteLine();

// Test TryGet performance
const int iterations = 100000;
SamplePayload? value;
bool found;

// Test Original
var sw = Stopwatch.StartNew();
for (int i = 0; i < iterations; i++)
{
    found = original.TryGet<SamplePayload>(testKey, out value);
}
sw.Stop();
var originalNs = (sw.Elapsed.TotalNanoseconds / iterations);
Console.WriteLine($"Original (MethodCacheAdapter):          {originalNs:F1} ns/op ({sw.ElapsedMilliseconds}ms total)");

// Test FixedSimple
sw.Restart();
for (int i = 0; i < iterations; i++)
{
    found = fixedSimple.TryGet<SamplePayload>(testKey, out value);
}
sw.Stop();
var fixedSimpleNs = (sw.Elapsed.TotalNanoseconds / iterations);
Console.WriteLine($"FixedSimple (SimpleFixedAdapter):       {fixedSimpleNs:F1} ns/op ({sw.ElapsedMilliseconds}ms total)");

// Test Optimized
sw.Restart();
for (int i = 0; i < iterations; i++)
{
    found = optimized.TryGet<SamplePayload>(testKey, out value);
}
sw.Stop();
var optimizedNs = (sw.Elapsed.TotalNanoseconds / iterations);
Console.WriteLine($"Optimized (ProperlyOptimizedAdapter):   {optimizedNs:F1} ns/op ({sw.ElapsedMilliseconds}ms total)");

// Test Baseline
sw.Restart();
for (int i = 0; i < iterations; i++)
{
    found = baseline.TryGet<SamplePayload>(testKey, out value);
}
sw.Stop();
var baselineNs = (sw.Elapsed.TotalNanoseconds / iterations);
Console.WriteLine($"Baseline (MemoryCache):                 {baselineNs:F1} ns/op ({sw.ElapsedMilliseconds}ms total)");

Console.WriteLine();
Console.WriteLine("Performance comparison:");
Console.WriteLine($"  Original vs Baseline:     {originalNs / baselineNs:F2}x slower");
Console.WriteLine($"  FixedSimple vs Baseline:  {fixedSimpleNs / baselineNs:F2}x slower");
Console.WriteLine($"  Optimized vs Baseline:    {optimizedNs / baselineNs:F2}x slower");
Console.WriteLine($"  Optimized vs Original:    {originalNs / optimizedNs:F2}x faster");

// Cleanup
original.Dispose();
fixedSimple.Dispose();
optimized.Dispose();
baseline.Dispose();
