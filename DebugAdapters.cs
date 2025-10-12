using System;
using System.Diagnostics;
using MethodCache.Benchmarks.Comparison.Adapters;
using MethodCache.Benchmarks.Comparison;

Console.WriteLine("Testing cache adapters...\n");

// Create adapters
var methodCache = new MethodCacheAdapter();
var optimized = new OptimizedMethodCacheAdapter();
var memoryCache = new MemoryCacheAdapter();

// Test data
var testKey = "test_key";
var testValue = new SamplePayload { Id = 1, Name = "Test", Data = new byte[100] };

// Warm up
methodCache.Set(testKey, testValue, TimeSpan.FromMinutes(1));
optimized.Set(testKey, testValue, TimeSpan.FromMinutes(1));
memoryCache.Set(testKey, testValue, TimeSpan.FromMinutes(1));

// Wait for sets to complete
System.Threading.Thread.Sleep(100);

// Test TryGet performance
const int iterations = 10000;
SamplePayload? value;
bool found;

// Test MethodCache
var sw = Stopwatch.StartNew();
for (int i = 0; i < iterations; i++)
{
    found = methodCache.TryGet<SamplePayload>(testKey, out value);
}
sw.Stop();
Console.WriteLine($"MethodCache: {sw.ElapsedMilliseconds}ms for {iterations} iterations");

// Test Optimized
sw.Restart();
for (int i = 0; i < iterations; i++)
{
    found = optimized.TryGet<SamplePayload>(testKey, out value);
}
sw.Stop();
Console.WriteLine($"Optimized:   {sw.ElapsedMilliseconds}ms for {iterations} iterations");

// Test MemoryCache
sw.Restart();
for (int i = 0; i < iterations; i++)
{
    found = memoryCache.TryGet<SamplePayload>(testKey, out value);
}
sw.Stop();
Console.WriteLine($"MemoryCache: {sw.ElapsedMilliseconds}ms for {iterations} iterations");

// Verify all found the value
Console.WriteLine("\nVerifying cache hits:");
Console.WriteLine($"MethodCache found: {methodCache.TryGet<SamplePayload>(testKey, out value)}");
Console.WriteLine($"Optimized found: {optimized.TryGet<SamplePayload>(testKey, out value)}");
Console.WriteLine($"MemoryCache found: {memoryCache.TryGet<SamplePayload>(testKey, out value)}");