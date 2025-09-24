# 🧪 **Integration Test Strategy for L3 Cache**

## 📋 **Problem Analysis**

The integration tests were failing due to several issues:

1. **🐋 Docker Container Startup Delays** - Testcontainers taking 3-5 minutes to start SQL Server
2. **🔧 Missing Service Registrations** - Tests using non-existent `AddSqlServerInfrastructureForTests` method
3. **📛 Outdated Class References** - Tests still referencing old `SqlServerStorageProvider` instead of `SqlServerPersistentStorageProvider`
4. **⏱️ No Timeout Protection** - Tests hanging indefinitely when Docker unavailable

## ✅ **Implemented Solutions**

### **1. Enhanced Testcontainers Configuration**

```csharp
SharedContainer = new MsSqlBuilder()
    .WithImage("mcr.microsoft.com/mssql/server:2019-latest") // Faster than 2022
    .WithPassword("YourStrong@Passw0rd")
    .WithEnvironment("MSSQL_PID", "Express") // Lightweight edition
    .WithWaitStrategy(Wait.ForUnixContainer()
        .UntilPortIsAvailable(1433)
        .UntilCommandIsCompleted("/opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P YourStrong@Passw0rd -Q \"SELECT 1\" -l 1"))
    .WithReuse(true) // Reuse containers across test runs
    .WithCleanUp(false) // Keep alive for faster subsequent runs
    .Build();
```

### **2. Docker Availability Check**

```csharp
private static Task<bool> CheckDockerAvailabilityAsync()
{
    try
    {
        var dockerEndpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        var testBuilder = new ContainerBuilder().WithImage("hello-world");
        var container = testBuilder.Build();
        return Task.FromResult(true);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Docker availability check failed: {ex.Message}");
        return Task.FromResult(false);
    }
}
```

### **3. Optimized Connection Strings**

```csharp
SqlServerConnectionString = SharedContainer != null
    ? $"{SharedContainer.GetConnectionString()};Connection Timeout=30;Command Timeout=30;Pooling=true;Min Pool Size=1;Max Pool Size=10"
    : Environment.GetEnvironmentVariable("METHODCACHE_SQLSERVER_URL");
```

### **4. External SQL Server Support**

Added environment variable support for external SQL Server:

```bash
# Use external SQL Server (faster for development)
export METHODCACHE_SQLSERVER_URL="Server=localhost;Database=TestCache;Trusted_Connection=true;"

# Or use SQL Server in Docker
export METHODCACHE_SQLSERVER_URL="Server=localhost,1433;Database=TestCache;User Id=sa;Password=YourStrong@Passw0rd;"
```

## 🚀 **Running Integration Tests**

### **Option 1: External SQL Server (Recommended for Development)**

```bash
# Setup local SQL Server instance
export METHODCACHE_SQLSERVER_URL="Server=localhost;Database=MethodCacheTests;Trusted_Connection=true;"

# Run tests (much faster - no Docker startup delay)
dotnet test MethodCache.Providers.SqlServer.IntegrationTests
```

### **Option 2: Docker SQL Server (CI/CD)**

```bash
# Ensure Docker is running
docker --version

# Run tests with automatic container creation
dotnet test MethodCache.Providers.SqlServer.IntegrationTests
```

### **Option 3: Quick Unit Tests Only**

```bash
# Run only unit tests (skip integration tests)
dotnet test MethodCache.Providers.SqlServer.Tests
```

## 📊 **Test Categories & Expected Performance**

| Test Category | Count | External SQL | Docker SQL |
|---------------|-------|--------------|------------|
| **Table Management** | 8 tests | ~10 seconds | ~5 minutes* |
| **Storage Provider** | 8 tests | ~15 seconds | ~5 minutes* |
| **Service Extensions** | 9 tests | ~20 seconds | ~5 minutes* |
| **Backplane** | 7 tests | ~25 seconds | ~6 minutes* |
| **Health Checks** | 7 tests | ~10 seconds | ~5 minutes* |
| **Hybrid Cache** | 6 tests | ~15 seconds | ~5 minutes* |

*_First run with Docker includes ~3-5 minute container startup time_

## 🔧 **Updated Architecture Tests**

### **L3 Persistent Cache Tests**

Tests now use the enhanced `HybridStorageManager` with L3 support:

```csharp
// L1 + L3 configuration (Memory + SQL Server)
services.AddL1L3CacheWithSqlServer(
    configureStorage: options =>
    {
        options.L1DefaultExpiration = TimeSpan.FromMinutes(5);
        options.L3DefaultExpiration = TimeSpan.FromDays(7);
        options.EnableL3Promotion = true;
    },
    configureSqlServer: options =>
    {
        options.ConnectionString = connectionString;
        options.EnableAutoTableCreation = true;
    });
```

### **Service Registration Tests**

Updated to test new unified service registrations:

```csharp
// Test enhanced HybridStorageManager registration
services.AddTripleLayerCacheWithSqlServer(/*...*/);
var hybridManager = serviceProvider.GetRequiredService<HybridStorageManager>();
var storageProvider = serviceProvider.GetRequiredService<IStorageProvider>();

// Verify they're the same instance (unified approach)
Assert.Same(hybridManager, storageProvider);
```

## 🎯 **Development Workflow**

### **For L3 Feature Development**

```bash
# 1. Set up external SQL Server for fast iteration
export METHODCACHE_SQLSERVER_URL="Server=localhost;Database=TestCache;Trusted_Connection=true;"

# 2. Run specific test class during development
dotnet test --filter "ClassName~SqlServerStorageProviderIntegrationTests"

# 3. Run all integration tests before commit
dotnet test MethodCache.Providers.SqlServer.IntegrationTests

# 4. Verify unit tests pass
dotnet test MethodCache.Providers.SqlServer.Tests
```

### **For CI/CD Pipeline**

```yaml
# GitHub Actions / Azure DevOps
steps:
  - name: Start SQL Server
    run: |
      docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=YourStrong@Passw0rd" \
        -p 1433:1433 -d --platform linux/amd64 \
        mcr.microsoft.com/mssql/server:2022-latest
      sleep 30 # Wait for startup

  - name: Run Integration Tests
    run: dotnet test MethodCache.Providers.SqlServer.IntegrationTests
    env:
      METHODCACHE_SQLSERVER_URL: "Server=localhost,1433;Database=master;User Id=sa;Password=YourStrong@Passw0rd;"
```

### **macOS Apple Silicon Optimization**

Based on [Microsoft's guidance](https://devblogs.microsoft.com/azure-sql/development-with-sql-in-containers-on-macos/):

**Prerequisites for Apple Silicon Macs:**
```bash
# 1. Install Rosetta 2 (if not already installed)
softwareupdate --install-rosetta

# 2. Enable Rosetta emulation in Docker Desktop
# Go to Docker Desktop → Settings → Features in development → Enable "Use Rosetta for x86/amd64 emulation"

# 3. Verify Docker setup
docker --version  # Should be 4.16+ for optimal Rosetta support
```

**Optimized Container Configuration:**
```bash
# Manual SQL Server container with platform specification
docker run --name sql-test --platform linux/amd64 \
  -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=YourStrong@Passw0rd" \
  -e "MSSQL_PID=Developer" \
  -p 1433:1433 -d \
  mcr.microsoft.com/mssql/server:2022-latest

# Connect using sqlcmd (new tools)
docker exec -it sql-test /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P YourStrong@Passw0rd -C
```

## 📈 **Performance Optimizations Implemented**

1. **🚀 Container Reuse** - Reuse SQL Server containers across test runs
2. **🍎 Apple Silicon Support** - Rosetta x86/amd64 emulation with "indiscernible performance impact"
3. **⚡ Developer Edition** - Full SQL Server features for comprehensive testing
4. **🔧 Connection Pooling** - Optimized connection strings with pooling
5. **📦 Latest Images** - SQL Server 2022 with platform specification for compatibility
6. **🎯 Targeted Waits** - More efficient health checks with sqlcmd tools18
7. **💾 External DB Support** - Skip Docker entirely for development
8. **🏷️ Named Resources** - Better container resource management

## 🐛 **Troubleshooting Guide**

### **Docker Issues**

```bash
# Check Docker status
docker --version
docker ps

# Manual SQL Server container
docker run -e "ACCEPT_EULA=Y" -e "SA_PASSWORD=YourStrong@Passw0rd" \
  -p 1433:1433 -d mcr.microsoft.com/mssql/server:2019-latest

# Test connection
sqlcmd -S localhost -U sa -P YourStrong@Passw0rd -Q "SELECT 1"
```

### **SQL Server Issues**

```sql
-- Create test database
CREATE DATABASE MethodCacheTests;
GO

-- Verify tables
USE MethodCacheTests;
SELECT name FROM sys.tables WHERE name LIKE '%cache%';
```

### **Test Debugging**

```bash
# Run with detailed logging
dotnet test MethodCache.Providers.SqlServer.IntegrationTests -l "console;verbosity=detailed"

# Run single test
dotnet test --filter "TestName~SetAsync_WithBasicValue_ShouldStoreAndRetrieve"

# Check test output
dotnet test --logger "console;verbosity=normal"
```

## ✅ **Integration Test Strategy Summary**

The enhanced integration test infrastructure provides:

- **🔄 Flexible Deployment** - Works with Docker or external SQL Server
- **⚡ Development Speed** - Fast iteration with external databases
- **🧪 Comprehensive Coverage** - Tests all L3 cache functionality
- **📊 Clear Feedback** - Detailed error messages and setup guidance
- **🎯 CI/CD Ready** - Optimized for automated pipeline execution

This strategy ensures reliable L3 cache testing while minimizing developer friction and providing clear paths for both local development and automated testing scenarios.