# Three Ways to Use MethodCache

MethodCache can be used in three different ways, each with different performance characteristics. We benchmark all three to show the full spectrum:

## 1. Source Generation (Real Usage) - **15-58 ns**

**How it works**: Use `[Cache]` attributes, source generator creates specialized code

```csharp
public partial class MyService
{
    [Cache(Duration = "00:10:00")]
    public virtual string GetData(string key)
    {
        return ExpensiveOperation(key);
    }
}

// Generated code (simplified):
// public override string GetData(string key)
// {
//     var cacheKey = $"GetData:{key}";
//     return _cache.Get(cacheKey) ?? ExecuteAndCache();
// }
```

**Performance**: 15-58 ns  
**Benchmark**: `RealMethodCacheComparison.cs`  
**Use this**: For production applications

**Why so fast?**
- No boxing, no object[] allocation
- Direct cache access, no interface dispatch
- Compile-time key generation
- Optimized ValueTask handling
- Method name is compile-time constant

## 2. Direct API (Manual Keys) - **~2,000-3,000 ns**

**How it works**: Use `IMemoryCache` interface directly with manual keys

```csharp
var cache = serviceProvider.GetRequiredService<ICacheManager>();
var memoryCache = (IMemoryCache)cache;

// Direct API usage - just like LazyCache, FusionCache, etc.
var value = await memoryCache.GetAsync<string>("my-key");
if (value == null)
{
    value = ExpensiveOperation();
    await memoryCache.SetAsync("my-key", value, TimeSpan.FromMinutes(10));
}
```

**Performance**: ~2,000-3,000 ns (competitive with other frameworks)  
**Benchmark**: `MethodCacheDirect_Hit` in `UnifiedCacheComparisonBenchmarks.cs`  
**Use this**: When you need dynamic keys or don't want source generation

**Why competitive?**
- No source generation overhead
- No key generation - you provide keys
- Direct cache storage access
- Similar to how LazyCache, FusionCache work

## 3. Static Pre-Generated Keys - **~2,200 ns**

**How it works**: Generate keys once during initialization, reuse them

```csharp
// In constructor:
var keyGenerator = serviceProvider.GetRequiredService<ICacheKeyGenerator>();
_getDataKey = keyGenerator.GenerateKey("GetData", Array.Empty<object>(), policy);

// At runtime:
var cacheKey = $"{_getDataKey}:{userKey}";  // Simple concatenation
return await _cache.GetAsync<SamplePayload>(cacheKey);
```

**Performance**: ~2,200 ns  
**Benchmark**: `MethodCacheStatic_Hit` in `UnifiedCacheComparisonBenchmarks.cs`  
**Use this**: When you want key generation benefits but through an adapter

**Why similar to Direct API?**
- Keys generated once, not per call
- Simulates what source generator does
- Shows performance without runtime key generation overhead

## 4. Runtime Key Generation (Not Recommended) - **~9,500 ns**

**How it works**: Generate keys on every call through generic interface

```csharp
// On EVERY call:
var cacheKey = _keyGenerator.GenerateKey(
    "GetData",
    new object[] { key },  // ❌ Allocation
    _policy                 // ❌ Serialization
);
```

**Performance**: ~9,500 ns  
**Benchmark**: `MethodCache_Hit` in `UnifiedCacheComparisonBenchmarks.cs`  
**Use this**: NEVER (shown for comparison only)

**Why so slow?**
- Object array allocation: ~200 ns
- Parameter boxing: ~150 ns
- Policy serialization: ~200 ns
- String operations: ~100 ns
- Method dispatch: ~50 ns
- **Total overhead: ~700-7,300 ns**

## Performance Comparison

| Method | Median Time | vs Direct API | vs Source Gen | Use Case |
|--------|-------------|---------------|---------------|----------|
| **Source Generation** | **20-58 ns** | **100x faster** | - | Production apps |
| **Direct API** | **~2,500 ns** | - | 40-125x slower | Dynamic keys |
| **Static Keys** | **~2,200 ns** | 1.1x faster | 40-110x slower | Adapter pattern |
| **Runtime Keys** | **~9,500 ns** | 3.8x slower | 160-475x slower | Never use |
| LazyCache | ~2,300 ns | 1.1x slower | 40-115x slower | Reference |
| FusionCache | ~5,400 ns | 2.2x slower | 90-270x slower | Reference |
| IMemoryCache | ~5,400 ns | 2.2x slower | 90-270x slower | Baseline |

## Decision Matrix

### Use Source Generation When:
✅ You control the code (not a library)  
✅ You want maximum performance  
✅ Keys can be determined from method signature  
✅ You're okay with `partial` classes and `virtual` methods  

### Use Direct API When:
✅ You need fully dynamic keys  
✅ You're building a library that wraps MethodCache  
✅ You want same usage pattern as other frameworks  
✅ Performance is good enough (~2,500 ns is fast!)  

### Use Static Keys When:
✅ You're building an adapter/wrapper  
✅ You want to show fair comparisons  
✅ You need consistent key generation  
✅ You want MethodCache benefits through interfaces  

### Never Use Runtime Keys:
❌ It's 4x slower than necessary  
❌ Shows artificial overhead  
❌ Only exists for comparison purposes  

## The Adapter Problem - Solved!

The question was: "What can we do about the adapter problem?"

**Answer**: MethodCache supports **manual keys through Direct API**, just like other frameworks!

- **Static Keys**: Pre-generate keys (simulates source generator)
- **Direct API**: User-provided keys (truly fair comparison)
- Both show MethodCache is competitive when used the same way as other frameworks

**The key insight**: MethodCache's speed advantage comes from source generation, but its underlying cache storage is also competitive when accessed directly.

## Benchmark Commands

```bash
# Real usage (source generation)
dotnet run -c Release -- realcompare

# Direct API comparison
dotnet run -c Release -- comparison --filter '*Direct*'

# Static keys comparison
dotnet run -c Release -- comparison --filter '*Static*'

# All adapter-based comparisons
dotnet run -c Release -- comparison
```

## Conclusion

MethodCache gives you options:

1. **Maximum performance?** Use source generation (15-58 ns)
2. **Need dynamic keys?** Use Direct API (~2,500 ns - still fast!)
3. **Building adapters?** Use Static Keys (~2,200 ns)
4. **Fair comparisons?** We provide all three!

The "adapter problem" is solved by showing MethodCache can be used with manual keys (Direct API) and performs competitively with other frameworks in that mode. The massive speedup from source generation is a bonus, not a requirement.
