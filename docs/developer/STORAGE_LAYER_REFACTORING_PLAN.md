# Storage Layer Refactoring Plan

## Current State Analysis

### HybridStorageManager.cs (970 lines)

**Responsibilities (7 major concerns):**

1. **L1 Memory Layer** (lines 109-122, 213-214, etc.)
   - Fast memory cache access
   - Statistics tracking (_l1Hits, _l1Misses)
   - Expiration calculation

2. **L2 Distributed Layer** (lines 124-160, 500-527, etc.)
   - Remote cache access (Redis, etc.)
   - Semaphore-based concurrency control
   - L1 promotion on hits
   - Async write support

3. **L3 Persistent Layer** (lines 162-197, 693-720, etc.)
   - Persistent storage access (SQL Server, etc.)
   - Semaphore-based concurrency control
   - L1/L2 promotion on hits
   - Async write support

4. **Tag Index** (lines 602-679)
   - Tag-to-key forward index
   - Key-to-tag reverse index
   - Tag invalidation
   - O(K) complexity optimization

5. **Backplane Coordination** (lines 88-106, 565-600)
   - Subscribe to invalidation messages
   - Publish invalidation messages
   - Cross-instance coordination
   - Statistics tracking

6. **Async Write Queue** (lines 73-86, 807-891)
   - Bounded channel for async writes
   - Worker task processing
   - Semaphore scheduling
   - Graceful shutdown

7. **Health & Metrics** (lines 392-486)
   - Multi-layer health checks
   - Statistics aggregation
   - Hit/miss ratios
   - Response times

---

## Target Architecture

### Layer Components (7 independent files)

```
MethodCache.Core/Storage/Layers/
├── IStorageLayer.cs              (~50 lines)  - Core layer interface
├── MemoryStorageLayer.cs         (~150 lines) - L1 implementation
├── DistributedStorageLayer.cs    (~180 lines) - L2 implementation
├── PersistentStorageLayer.cs     (~180 lines) - L3 implementation
├── TagIndexLayer.cs              (~120 lines) - Tag tracking
├── BackplaneCoordinationLayer.cs (~150 lines) - Cross-instance sync
└── AsyncWriteQueueLayer.cs       (~100 lines) - Async write handling

MethodCache.Core/Storage/
├── StorageCoordinator.cs         (~200 lines) - Thin orchestrator
└── HybridStorageManager.cs       (DEPRECATED - kept for compatibility)
```

---

## Step 1: Define IStorageLayer Interface

### Core Abstraction

```csharp
namespace MethodCache.Core.Storage.Layers;

/// <summary>
/// Represents a single layer in the storage pipeline with its own metrics and lifecycle.
/// </summary>
public interface IStorageLayer
{
    /// <summary>
    /// Gets the unique identifier for this layer (e.g., "L1", "L2", "TagIndex").
    /// </summary>
    string LayerId { get; }

    /// <summary>
    /// Gets the priority of this layer (lower = executed first).
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets whether this layer is enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Initializes the layer asynchronously.
    /// </summary>
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a Get operation, optionally modifying the context.
    /// </summary>
    /// <returns>True if the operation was handled and should stop propagating, false to continue.</returns>
    ValueTask<StorageLayerResult<T>> GetAsync<T>(StorageContext context, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Handles a Set operation.
    /// </summary>
    ValueTask SetAsync<T>(StorageContext context, string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken);

    /// <summary>
    /// Handles a Remove operation.
    /// </summary>
    ValueTask RemoveAsync(StorageContext context, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Handles a RemoveByTag operation.
    /// </summary>
    ValueTask RemoveByTagAsync(StorageContext context, string tag, CancellationToken cancellationToken);

    /// <summary>
    /// Checks if a key exists in this layer.
    /// </summary>
    ValueTask<bool> ExistsAsync(StorageContext context, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Gets health status for this layer.
    /// </summary>
    ValueTask<LayerHealthStatus> GetHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets statistics for this layer.
    /// </summary>
    LayerStats GetStats();

    /// <summary>
    /// Disposes the layer asynchronously.
    /// </summary>
    ValueTask DisposeAsync();
}
```

### Supporting Types

```csharp
/// <summary>
/// Result from a storage layer operation.
/// </summary>
public readonly struct StorageLayerResult<T>
{
    public T? Value { get; init; }
    public bool Found { get; init; }
    public bool StopPropagation { get; init; } // True = stop pipeline, false = continue to next layer

    public static StorageLayerResult<T> Hit(T value) => new() { Value = value, Found = true, StopPropagation = true };
    public static StorageLayerResult<T> Miss() => new() { Found = false, StopPropagation = false };
    public static StorageLayerResult<T> NotHandled() => new() { Found = false, StopPropagation = false };
}

/// <summary>
/// Context passed through the storage pipeline.
/// </summary>
public class StorageContext
{
    public string OperationId { get; init; } = Guid.NewGuid().ToString();
    public DateTimeOffset StartTime { get; init; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Metadata { get; } = new();

    // Track which layers have been hit/miss
    public HashSet<string> LayersHit { get; } = new();
    public HashSet<string> LayersMissed { get; } = new();

    // Tags tracked during the operation
    public string[]? Tags { get; set; }
}

/// <summary>
/// Health status for a layer.
/// </summary>
public record LayerHealthStatus(
    string LayerId,
    HealthStatus Status,
    string? Message = null,
    Dictionary<string, object>? Details = null);

/// <summary>
/// Statistics for a layer.
/// </summary>
public record LayerStats(
    string LayerId,
    long Hits,
    long Misses,
    double HitRatio,
    long Operations,
    Dictionary<string, object>? AdditionalStats = null);
```

---

## Step 2: Extract MemoryStorageLayer (L1)

### Responsibilities
- Fast in-memory cache access
- Hit/miss tracking
- Delegates to existing `IMemoryStorage`

### Implementation Outline

```csharp
public class MemoryStorageLayer : IStorageLayer
{
    private readonly IMemoryStorage _memoryStorage;
    private readonly StorageLayerOptions _options;
    private readonly ILogger<MemoryStorageLayer> _logger;
    private readonly ICacheMetricsProvider? _metricsProvider;

    private long _hits;
    private long _misses;

    public string LayerId => "L1";
    public int Priority => 10;
    public bool IsEnabled => true; // Always enabled

    public async ValueTask<StorageLayerResult<T>> GetAsync<T>(StorageContext context, string key, CancellationToken cancellationToken)
    {
        var result = await _memoryStorage.GetAsync<T>(key, cancellationToken);

        if (result != null)
        {
            Interlocked.Increment(ref _hits);
            context.LayersHit.Add(LayerId);
            _metricsProvider?.CacheHit($"HybridStorage:{LayerId}");
            _logger.LogDebug("L1 cache hit for key {Key}", key);
            return StorageLayerResult<T>.Hit(result);
        }

        Interlocked.Increment(ref _misses);
        context.LayersMissed.Add(LayerId);
        _metricsProvider?.CacheMiss($"HybridStorage:{LayerId}");
        return StorageLayerResult<T>.Miss();
    }

    // ... other methods
}
```

---

## Step 3: Extract DistributedStorageLayer (L2)

### Responsibilities
- Remote cache access (Redis, etc.)
- Semaphore-based concurrency control
- L1 promotion on cache hits
- Async write support

### Key Features
- Wraps `IStorageProvider`
- Coordinates with `MemoryStorageLayer` for promotion
- Handles async writes via `AsyncWriteQueueLayer`

---

## Step 4: Extract PersistentStorageLayer (L3)

### Responsibilities
- Persistent storage access (SQL Server, etc.)
- Semaphore-based concurrency control
- L1/L2 promotion on cache hits
- Async write support

### Key Features
- Wraps `IPersistentStorageProvider`
- Coordinates with higher layers for promotion
- Different expiration calculation (longer TTL)

---

## Step 5: Extract TagIndexLayer

### Responsibilities
- Maintain forward index: key → tags
- Maintain reverse index: tag → keys
- Handle tag invalidation efficiently (O(K) complexity)
- Track tags across operations

### Key Features
- Uses `ConcurrentDictionary` for thread-safe tracking
- Wraps all operations to capture tags
- Provides tag lookup for promotions

---

## Step 6: Extract BackplaneCoordinationLayer

### Responsibilities
- Subscribe to cross-instance invalidation messages
- Publish invalidation messages to other instances
- Coordinate L1 invalidation based on backplane events
- Track backplane message statistics

### Key Features
- Wraps `IBackplane`
- Coordinates with `MemoryStorageLayer` for invalidations
- Handles graceful shutdown

---

## Step 7: Extract AsyncWriteQueueLayer

### Responsibilities
- Queue async writes for L2/L3
- Worker task processing
- Semaphore scheduling
- Graceful shutdown

### Key Features
- Bounded channel for work items
- Configurable capacity
- TaskCompletionSource for awaitable writes
- Cancellation support

---

## Step 8: Create StorageCoordinator

### Responsibilities (Thin Orchestrator)
- Initialize all layers
- Execute pipeline for Get/Set/Remove operations
- Aggregate health status
- Aggregate statistics
- Coordinate disposal

### Implementation Outline

```csharp
public class StorageCoordinator : IStorageProvider, IAsyncDisposable
{
    private readonly IReadOnlyList<IStorageLayer> _layers;
    private readonly ILogger<StorageCoordinator> _logger;

    public StorageCoordinator(IEnumerable<IStorageLayer> layers, ILogger<StorageCoordinator> logger)
    {
        _layers = layers.OrderBy(l => l.Priority).ToList();
        _logger = logger;
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var context = new StorageContext();

        foreach (var layer in _layers.Where(l => l.IsEnabled))
        {
            var result = await layer.GetAsync<T>(context, key, cancellationToken);

            if (result.Found)
            {
                return result.Value;
            }

            if (result.StopPropagation)
            {
                break;
            }
        }

        return default;
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan expiration, IEnumerable<string> tags, CancellationToken cancellationToken = default)
    {
        var context = new StorageContext { Tags = tags.ToArray() };

        // Execute all enabled layers (don't stop on first)
        var tasks = _layers
            .Where(l => l.IsEnabled)
            .Select(l => l.SetAsync(context, key, value, expiration, tags, cancellationToken).AsTask());

        await Task.WhenAll(tasks);
    }

    // ... other methods
}
```

---

## Migration Strategy

### Phase 1: Create Layer Infrastructure ✅ (This step)
1. Define `IStorageLayer` interface
2. Define supporting types (`StorageContext`, `StorageLayerResult`, etc.)
3. Add new `Layers/` directory under `MethodCache.Core/Storage/`

### Phase 2: Extract Individual Layers
1. Extract `MemoryStorageLayer` (simplest)
2. Extract `TagIndexLayer` (no external dependencies)
3. Extract `AsyncWriteQueueLayer` (standalone concern)
4. Extract `DistributedStorageLayer` (depends on MemoryStorageLayer)
5. Extract `PersistentStorageLayer` (depends on MemoryStorageLayer + DistributedStorageLayer)
6. Extract `BackplaneCoordinationLayer` (depends on MemoryStorageLayer)

### Phase 3: Create Coordinator
1. Implement `StorageCoordinator`
2. Add DI registration helpers
3. Wire up all layers in correct priority order

### Phase 4: Deprecate HybridStorageManager
1. Mark `HybridStorageManager` as `[Obsolete]`
2. Create adapter: `HybridStorageManager` → `StorageCoordinator` (for backward compatibility)
3. Update DI registration to use `StorageCoordinator` by default
4. Keep `HybridStorageManager` for one major version

### Phase 5: Update Tests
1. Add layer-specific unit tests
2. Add integration tests for coordinator
3. Verify all 640 tests still pass
4. Add new tests for layer isolation

---

## Benefits

### 1. **Testability**
- Each layer can be tested independently
- Mock individual layers for integration tests
- Test coordinator with fake layers

### 2. **Extensibility**
- Add new layers without modifying existing ones
- Compose custom pipelines per scenario
- Easy to add metrics/logging/tracing per layer

### 3. **Maintainability**
- Each file ~100-180 lines (was 970)
- Single responsibility per layer
- Clear separation of concerns

### 4. **Performance**
- No performance regression (same logic, different organization)
- Better observability per layer
- Easier to identify bottlenecks

### 5. **Backward Compatibility**
- Keep `HybridStorageManager` as adapter
- Zero breaking changes for consumers
- Gradual migration path

---

## Success Criteria

1. ✅ All 640 tests pass after refactoring
2. ✅ Zero breaking changes in public API
3. ✅ Each layer file < 200 lines
4. ✅ `StorageCoordinator` < 250 lines
5. ✅ All layers independently testable
6. ✅ Performance regression < 5%
7. ✅ Memory overhead < 1KB per request
8. ✅ Documentation updated

---

## Timeline Estimate

| Phase | Task | Estimated Time |
|-------|------|----------------|
| 1 | Define interfaces | 2 hours |
| 2.1 | Extract MemoryStorageLayer | 3 hours |
| 2.2 | Extract TagIndexLayer | 3 hours |
| 2.3 | Extract AsyncWriteQueueLayer | 3 hours |
| 2.4 | Extract DistributedStorageLayer | 4 hours |
| 2.5 | Extract PersistentStorageLayer | 4 hours |
| 2.6 | Extract BackplaneCoordinationLayer | 4 hours |
| 3 | Create StorageCoordinator | 4 hours |
| 4 | Deprecate HybridStorageManager | 2 hours |
| 5 | Update tests | 4 hours |
| | **Total** | **33 hours (~1 week)** |

---

**Status:** Planning Complete - Ready to implement
**Next Step:** Define `IStorageLayer` interface and supporting types
