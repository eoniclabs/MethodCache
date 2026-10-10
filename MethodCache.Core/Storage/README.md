# Storage Architecture

MethodCache storage is organized around **three cache layers** that work together to provide fast, scalable, and reliable caching.

## 🏗️ Layer Architecture

```
┌─────────────────────────────────────────┐
│         StorageCoordinator              │  ← Multi-layer orchestration
│  (Coordination/)                        │
├─────────────────────────────────────────┤
│  L1: Memory     │ In-process, fastest   │  ← Memory/
│  L2: Distributed│ Redis, etc.           │  ← Distributed/
│  L3: Persistent │ SQL Server, etc.      │  ← Persistent/
└─────────────────────────────────────────┘
```

## 📁 Folder Structure

### Memory/ - L1 In-Memory Cache
**Fastest layer** - in-process memory caching

- `MemoryStorage.cs` - Core memory storage implementation
- `MemoryStorageLayer.cs` - Layer wrapper for coordination

**Characteristics:**
- ⚡ Ultra-fast (nanoseconds)
- 🔄 Per-instance (not shared)
- 💾 Limited by process memory
- ✅ Use for: Hot data, high-frequency reads

---

### Distributed/ - L2 Distributed Cache
**Shared layer** - cross-instance distributed caching (Redis, etc.)

- `DistributedStorageLayer.cs` - Layer wrapper for distributed providers

**Characteristics:**
- 🌐 Shared across instances
- ⚡ Fast (milliseconds)
- 💾 Larger capacity than L1
- ✅ Use for: Shared data, multi-instance deployments

---

### Persistent/ - L3 Persistent Cache
**Durable layer** - persistent storage (SQL Server, etc.)

- `PersistentStorageLayer.cs` - Layer wrapper for persistent providers

**Characteristics:**
- 💾 Durable (survives restarts)
- 🌐 Shared across instances
- ⏱️ Slower than L1/L2 (milliseconds to seconds)
- ✅ Use for: Long-lived data, expensive computations

---

### Coordination/ - Multi-Layer Orchestration
**Coordinates** all layers to work together seamlessly

#### Core Files:
- `StorageCoordinator.cs` - Main multi-layer coordinator
- `StorageCoordinatorFactory.cs` - Factory for creating coordinators
- `HybridCacheManager.cs` - High-level cache manager
- `HybridCacheOptions.cs` - Configuration options

#### Coordination/Layers/ - Layer Infrastructure:
- `IStorageLayer.cs` - Core layer interface
- `StorageContext.cs` - Execution context
- `StorageLayerResult.cs` - Result pattern
- `StorageLayerOptions.cs` - Layer configuration
- `LayerHealthStatus.cs` - Health reporting
- `LayerStats.cs` - Performance metrics

#### Coordination/Supporting/ - Cross-Cutting Layers:
- `TagIndexLayer.cs` - Tag-based invalidation (priority 5)
- `AsyncWriteQueueLayer.cs` - Background writes (priority 15)
- `BackplaneCoordinationLayer.cs` - Cross-instance sync (priority 100)

**Layer Execution Order** (by priority):
1. TagIndex (5) - Tag lookup
2. Memory (10) - L1 cache
3. AsyncQueue (15) - Queued writes
4. Distributed (20) - L2 cache
5. Persistent (30) - L3 cache
6. Backplane (100) - Cross-instance sync

---

### Abstractions/ - Storage Interfaces
**All storage contracts** in one place

Core Interfaces:
- `IStorageProvider.cs` - Base storage interface
- `IMemoryStorage.cs` - L1 memory interface
- `IPersistentStorageProvider.cs` - L3 persistence interface
- `IBackplane.cs` - Cross-instance messaging
- `IDistributedLock.cs` - Distributed locking
- `ISerializer.cs` - Serialization contract
- `ICacheWarmingService.cs` - Cache warming
- `IHybridCacheManager.cs` - High-level manager

---

## 🔄 How It Works

### Read Flow
```
1. Check L1 (Memory) → Hit? Return ✅
   ↓ Miss
2. Check L2 (Distributed) → Hit? Warm L1 + Return ✅
   ↓ Miss
3. Check L3 (Persistent) → Hit? Warm L1+L2 + Return ✅
   ↓ Miss
4. Execute factory → Cache in L3→L2→L1 → Return ✅
```

### Write Flow
```
1. Write to L1 (Memory) - Immediate
2. Write to L2 (Distributed) - Async or Sync
3. Write to L3 (Persistent) - Async or Sync
4. Publish to Backplane - Notify other instances
```

### Invalidation Flow
```
1. Remove from L1 (local)
2. Remove from L2 (shared)
3. Remove from L3 (persistent)
4. Publish invalidation to Backplane
5. Other instances receive message → Clear their L1
```

---

## 🧭 Navigation Guide

**I want to...**

- **Understand L1 memory caching** → `Memory/`
- **Add Redis support** → `Distributed/` (implement for specific provider)
- **Add SQL persistence** → `Persistent/` (implement for specific provider)
- **Understand multi-layer coordination** → `Coordination/StorageCoordinator.cs`
- **Find storage interfaces** → `Abstractions/`
- **Add a new layer** → Implement `IStorageLayer` from `Coordination/Layers/`
- **Understand tag invalidation** → `Coordination/Supporting/TagIndexLayer.cs`
- **Configure async writes** → `Coordination/Supporting/AsyncWriteQueueLayer.cs`
- **Cross-instance sync** → `Coordination/Supporting/BackplaneCoordinationLayer.cs`

---

## 🎯 Design Principles

1. **Layer Independence**: Each layer works standalone
2. **Priority-Based**: Layers execute in priority order (5→10→15→20→30→100)
3. **Fail-Safe**: If a layer fails, others continue
4. **Observable**: Each layer reports health and metrics
5. **Composable**: Enable/disable layers as needed

---

## 📊 Statistics

- **Total files**: 29
- **Abstractions**: 10 interfaces
- **L1 Memory**: 3 files
- **L2 Distributed**: 1 file
- **L3 Persistent**: 1 file
- **Coordination**: 5 core + 6 infrastructure + 3 supporting = 14 files

---

**Last Updated**: 2025-10-10
**Architecture**: Layer-First Storage Organization
