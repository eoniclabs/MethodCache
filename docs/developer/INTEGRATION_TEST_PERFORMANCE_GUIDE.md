# ⚡ Integration Test Performance Guide

Comprehensive guide for optimizing MethodCache integration test performance across different platforms and configurations.

## 📋 Table of Contents

- [Performance Overview](#-performance-overview)
- [Setup Optimization](#-setup-optimization)
- [Platform-Specific Tuning](#-platform-specific-tuning)
- [Container Optimization](#-container-optimization)
- [Network Performance](#-network-performance)
- [Monitoring and Profiling](#-monitoring-and-profiling)
- [Troubleshooting Performance](#-troubleshooting-performance)

## 📊 Performance Overview



## 🚀 Setup Optimization

### Optimal Configuration Detection

#### Automatic Detection
```bash
# Run the performance analyzer
dotnet run --project Tests/MethodCache.Tests.Infrastructure perf

# Sample output:
# 🚀 Performance Analysis
# Platform: macOS (Arm64)
# SQL Server: External (detected)
# Redis: External (detected)
# Estimated test performance: 🚀 Excellent (~30 seconds)
```

#### Manual Verification
```bash
# Check Redis availability
redis-cli ping  # Should return PONG

# Check SQL Server availability
sqlcmd -S localhost -E -Q "SELECT 1"  # Should return 1

# Validate configuration
dotnet run --project Tests/MethodCache.Tests.Infrastructure validate
```

### Service Installation Optimization

#### Redis Installation

**macOS (Homebrew):**
```bash
# Install and configure Redis for optimal performance
brew install redis

# Configure Redis for development
echo "maxmemory 256mb" >> /usr/local/etc/redis.conf
echo "maxmemory-policy allkeys-lru" >> /usr/local/etc/redis.conf

# Start as service
brew services start redis

# Verify performance
redis-benchmark -h localhost -p 6379 -q -t ping,set,get
```

**Windows (Chocolatey):**
```powershell
# Install Redis for Windows
choco install redis-64

# Configure for performance
$redisConf = "C:\ProgramData\chocolatey\lib\redis-64\tools\redis.windows-service.conf"
Add-Content $redisConf "`nmaxmemory 256mb"
Add-Content $redisConf "`nmaxmemory-policy allkeys-lru"

# Restart service
Restart-Service Redis
```

**Linux (Package Manager):**
```bash
# Ubuntu/Debian
sudo apt-get install redis-server

# Configure for performance
sudo sed -i 's/# maxmemory <bytes>/maxmemory 256mb/' /etc/redis/redis.conf
sudo sed -i 's/# maxmemory-policy noeviction/maxmemory-policy allkeys-lru/' /etc/redis/redis.conf

# Restart service
sudo systemctl restart redis-server
```

#### SQL Server Installation

**Windows (SQL Server Express):**
```powershell
# Download and install SQL Server Express
choco install sql-server-express

# Configure for development
sqlcmd -S . -E -Q "ALTER SERVER CONFIGURATION SET PROCESS AFFINITY CPU = AUTO"
sqlcmd -S . -E -Q "EXEC sp_configure 'max server memory', 2048; RECONFIGURE"

# Create test database
sqlcmd -S . -E -Q "CREATE DATABASE MethodCacheTests"
```



**Linux (Native Installation):**
```bash
# Add Microsoft repository
curl -sSL https://packages.microsoft.com/keys/microsoft.asc | sudo apt-key add -
sudo add-apt-repository "$(curl -sSL https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/mssql-server-2022.list)"

# Install and configure
sudo apt-get update && sudo apt-get install -y mssql-server
sudo /opt/mssql/bin/mssql-conf setup

# Optimize for development
sudo /opt/mssql/bin/mssql-conf set memory.memorylimitmb 2048
sudo systemctl restart mssql-server
```

### Environment Configuration

#### Connection String Optimization
```bash
# SQL Server - optimized connection string
export METHODCACHE_SQLSERVER_URL="Server=localhost;Database=MethodCacheTests;Trusted_Connection=true;Connection Timeout=5;Command Timeout=30;Pooling=true;Min Pool Size=5;Max Pool Size=20;"

# Redis - optimized connection string
export METHODCACHE_REDIS_URL="localhost:6379,abortConnect=false,connectTimeout=5000,syncTimeout=5000"
```

#### Performance Environment Variables
```bash
# Enable performance optimizations
export METHODCACHE_PERFORMANCE_MODE=true
export METHODCACHE_PARALLEL_TESTS=true
export METHODCACHE_SKIP_CLEANUP=true  # For development only
```





## 🌐 Network Performance



#### Connection Pool Optimization
```csharp
// SQL Server connection string optimization
var connectionString = "Server=localhost;Database=MethodCacheTests;" +
                      "Trusted_Connection=true;" +
                      "Connection Timeout=5;" +
                      "Command Timeout=30;" +
                      "Pooling=true;" +
                      "Min Pool Size=5;" +
                      "Max Pool Size=100;" +
                      "Enlist=false;" +
                      "ConnectRetryCount=3;" +
                      "ConnectRetryInterval=10;";

// Redis connection optimization
var redisConfig = new ConfigurationOptions
{
    EndPoints = { "localhost:6379" },
    ConnectTimeout = 5000,
    SyncTimeout = 5000,
    AsyncTimeout = 5000,
    ConnectRetry = 3,
    ReconnectRetryPolicy = new ExponentialRetry(1000),
    KeepAlive = 60,
    AbortOnConnectFail = false
};
```

### Test Parallelization

#### Parallel Test Execution
```xml
<!-- test.runsettings -->
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>0</MaxCpuCount> <!-- Use all available cores -->
    <ResultsDirectory>./TestResults</ResultsDirectory>
  </RunConfiguration>

  <MSTest>
    <Parallelize>
      <Workers>0</Workers> <!-- Use all available cores -->
      <Scope>ClassLevel</Scope>
    </Parallelize>
  </MSTest>

  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="Code Coverage" uri="datacollector://Microsoft/CodeCoverage/2.0" assemblyQualifiedName="Microsoft.VisualStudio.Coverage.DynamicCoverageDataCollector, Microsoft.VisualStudio.TraceCollector, Version=11.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a">
        <Configuration>
          <CodeCoverage>
            <ModulePaths>
              <Include>
                <ModulePath>.*MethodCache.*\.dll$</ModulePath>
              </Include>
              <Exclude>
                <ModulePath>.*test.*\.dll$</ModulePath>
              </Exclude>
            </ModulePaths>
          </CodeCoverage>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

#### Test Isolation Optimization
```csharp
// Base test class with optimized setup
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected string TestKeyPrefix { get; private set; }

    public async Task InitializeAsync()
    {
        // Use unique prefix for parallel test isolation
        TestKeyPrefix = $"test:{Guid.NewGuid():N}:";

        // Parallel-safe initialization
        await InitializeServicesAsync();
    }

    private async Task InitializeServicesAsync()
    {
        // Use connection pooling for better performance
        var connectionPool = new ConnectionPool();
        await connectionPool.WarmupAsync();
    }
}
```

## 📊 Monitoring and Profiling

### Performance Monitoring

#### Test Execution Monitoring
```bash
# Run tests with detailed timing
dotnet test --logger "console;verbosity=detailed" --collect:"XPlat Code Coverage"

# Monitor with performance counters
dotnet-counters monitor --process-id $(pgrep -f "dotnet.*test") --counters System.Runtime,Microsoft.AspNetCore.Hosting
```

#### Custom Performance Metrics
```csharp
// Performance measurement in tests
public class PerformanceTestBase
{
    protected async Task<TimeSpan> MeasureAsync(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();

        // Log performance data
        Console.WriteLine($"Operation completed in {stopwatch.ElapsedMilliseconds}ms");

        return stopwatch.Elapsed;
    }
}
```



### Performance Profiling

#### .NET Profiling with dotnet-trace
```bash
# Profile test execution
dotnet-trace collect --process-id $(pgrep -f "dotnet.*test") --providers Microsoft-Windows-DotNETRuntime

# Analyze with PerfView or SpeedScope
dotnet-trace convert trace.nettrace --format speedscope
```

#### Database Performance Monitoring
```sql
-- SQL Server performance monitoring
SELECT
    s.session_id,
    r.status,
    r.command,
    r.cpu_time,
    r.total_elapsed_time,
    t.text AS query_text
FROM sys.dm_exec_sessions s
INNER JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE s.is_user_process = 1;
```

```bash
# Redis performance monitoring
redis-cli --latency-history -h localhost -p 6379
redis-cli info stats | grep -E "(keyspace_hits|keyspace_misses|total_commands_processed)"
```

## 🔧 Troubleshooting Performance

### Common Performance Issues



#### Connection Pool Exhaustion

**Symptoms:**
```
System.InvalidOperationException: Timeout expired. The timeout period elapsed
prior to obtaining a connection from the pool.
```

**Diagnosis:**
```sql
-- Check SQL Server connections
SELECT
    DB_NAME(database_id) as DatabaseName,
    COUNT(*) as ConnectionCount,
    login_name,
    program_name
FROM sys.dm_exec_sessions
WHERE is_user_process = 1
GROUP BY database_id, login_name, program_name;
```

**Solutions:**
```csharp
// Optimize connection string
var optimizedConnectionString =
    "Server=localhost;Database=MethodCacheTests;" +
    "Trusted_Connection=true;" +
    "Pooling=true;" +
    "Min Pool Size=10;" +
    "Max Pool Size=200;" +  // Increase pool size
    "Connection Timeout=30;" +
    "Command Timeout=120;";  // Increase timeouts
```



### Performance Debugging Tools

#### Test Performance Analysis
```bash
#!/bin/bash
# scripts/analyze-test-performance.sh

echo "Analyzing test performance..."

# Run tests with timing
time dotnet test MethodCache.Providers.*.IntegrationTests --logger "trx;LogFileName=results.trx"

# Extract timing information
grep -E "(Test|Time)" TestResults/results.trx | \
  sed 's/<[^>]*>//g' | \
  sort -k2 -nr | \
  head -20

echo "Slowest tests identified above"
```



### Performance Optimization Checklist

#### Initial Setup ✅
- [ ] External services installed and running
- [ ] Connection strings optimized
- [ ] Test parallelization enabled
- [ ] Performance monitoring in place

#### Regular Maintenance ✅
- [ ] Database statistics updated
- [ ] Connection pools monitored
- [ ] Resource usage analyzed
- [ ] Performance regressions detected

#### Troubleshooting ✅
- [ ] Resource usage monitored
- [ ] Network latency measured
- [ ] Database performance analyzed
- [ ] Test execution profiled

---

## 📚 Performance Resources

### Monitoring Tools
- **Container Monitoring:** Docker Desktop, Portainer, cAdvisor
- **.NET Profiling:** dotnet-trace, dotnet-counters, PerfView
- **Database Monitoring:** SQL Server Management Studio, Azure Data Studio
- **Redis Monitoring:** Redis CLI, RedisInsight

### Benchmarking Tools
- **Database:** SQLQueryStress, HammerDB
- **Redis:** redis-benchmark, memtier_benchmark
- **.NET:** BenchmarkDotNet, NBomber

### External Resources
- [SQL Server Performance Tuning](https://docs.microsoft.com/en-us/sql/relational-databases/performance/)
- [Redis Performance Optimization](https://redis.io/docs/management/optimization/)
- [.NET Performance Guidelines](https://docs.microsoft.com/en-us/dotnet/framework/performance/)

This performance guide ensures your MethodCache integration tests run as fast as possible across all platforms and configurations.atforms and configurations.