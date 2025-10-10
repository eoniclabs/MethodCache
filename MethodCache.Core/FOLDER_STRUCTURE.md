# MethodCache.Core Folder Structure

This document describes the organization of MethodCache.Core for easy navigation and understanding.

## 📁 Folder Overview

### Root Files (Entry Points)
- `MethodCacheServiceCollectionExtensions.cs` - Main DI registration entry point
- `MethodCacheRegistrationOptions.cs` - Core registration options

---

### 📁 Abstractions/
**Public contracts that developers interact with**

- `ICacheManager.cs` - Core caching operations interface
- `ICacheKeyGenerator.cs` - Cache key generation contract
- `ICacheKeyProvider.cs` - Key provider interface
- `ICacheMetricsProvider.cs` - Metrics provider contract
- `ICacheStats.cs` - Cache statistics interface
- `IMemoryCache.cs` - Memory cache interface

---

### 📁 Configuration/
**Configuration models and services**

Root configuration files and subfolders for specialized configuration aspects.

#### Configuration/Attributes/
- `CacheAttribute.cs` - Main `[Cache]` attribute
- `CacheInvalidateAttribute.cs` - `[CacheInvalidate]` attribute
- `Any.cs` - Special type for wildcard matching

#### Configuration/Abstractions/
- Configuration service interfaces

#### Configuration/Builders/
- Fluent configuration builders

#### Configuration/Fluent/
- Fluent API implementation

#### Configuration/Runtime/
- Runtime override support

#### Configuration/Sources/
- Legacy configuration sources (pre-PolicyPipeline)

---

### 📁 PolicyPipeline/
**New policy-based configuration architecture**

Modern configuration system using priority-based policy resolution.

#### PolicyPipeline/Sources/
- AttributePolicySource
- ConfigFilePolicySource
- FluentPolicySource
- RuntimeOverridePolicySource

#### PolicyPipeline/Model/
- CachePolicy models and mappers

#### PolicyPipeline/Resolution/
- PolicyResolver, PolicyRegistry

#### PolicyPipeline/Diagnostics/
- PolicyDiagnosticsService

See [PolicyPipeline/README.md](PolicyPipeline/README.md) for details.

---

### 📁 Storage/
**Storage layer implementation**

Multi-layer caching with L1 (memory), L2 (distributed), L3 (persistent).

#### Storage/Layers/
- Individual storage layer implementations
- Priority-based composition

---

### 📁 Keys/
**Key generation strategies**

- `FastHashKeyGenerator.cs` - Fast binary hash (production)
- `JsonKeyGenerator.cs` - Human-readable JSON (debugging)
- `MessagePackKeyGenerator.cs` - Efficient binary serialization
- `SmartKeyGenerator.cs` - Adaptive key generation

---

### 📁 Execution/
**Runtime execution context**

- `CacheExecutionContext.cs` - Execution context for cache operations
- `CacheContext.cs` - Cache context holder
- `CacheContextExtensions.cs` - Context extension methods

---

### 📁 Extensions/
**Extension methods**

- `CacheManagerExtensions.cs` - ICacheManager extensions
- `ExpressionBasedCacheExtensions.cs` - Expression-based helpers
- `ServiceCollectionExtensions.cs` - DI extensions

---

### 📁 Fluent/
**High-level fluent API**

- `CacheBuilder.cs` - Fluent cache configuration builder

---

### 📁 Options/
**Option models**

- `CacheEntryOptions.cs` - Cache entry configuration
- `CacheLookupResult.cs` - Lookup result model
- `DistributedLockOptions.cs` - Lock configuration
- `StampedeProtectionOptions.cs` - Stampede protection settings
- `StreamCacheOptions.cs` - Stream caching options

---

### 📁 Metrics/
**Metrics and monitoring**

- `ICacheMetrics.cs` - Metrics interface

---

### 📁 Utilities/
**Shared utility classes**

- `MemoryUsageCalculator.cs` - Memory usage calculations
- `StripedLockPool.cs` - High-performance locking

---

### 📁 Runtime/
**Runtime configuration (legacy)**

Contains runtime configuration sources. Some functionality moved to PolicyPipeline/Sources/.

---

### 📁 Documentation/
**Internal documentation**

- `MEMORY_USAGE_CALCULATION.md` - Memory calculation algorithms

---

## 🧭 Quick Navigation Guide

**I want to...**

- **Add a new cache attribute** → `Configuration/Attributes/`
- **Understand the policy pipeline** → `PolicyPipeline/README.md`
- **Implement a custom key generator** → `Keys/`
- **Configure cache behavior** → `Configuration/`
- **Extend storage layers** → `Storage/Layers/`
- **Add DI registration** → Root `MethodCacheServiceCollectionExtensions.cs`
- **Debug policy resolution** → `PolicyPipeline/Diagnostics/`
- **Add metrics** → `Metrics/`

---

## 📊 Statistics

- **Total folders**: 13 top-level + subfolders
- **Root files**: 2 (entry points only)
- **Total C# files**: 113
- **Organization**: Separated by concern and architectural layer

---

**Last Updated**: 2025-10-10
**Architecture**: Policy Pipeline + Layered Storage
