# MethodCache.SqlServer

SQL Server as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache through a table that each instance polls.

## Usage

<!-- snippet: sqlserver-basic -->
```cs
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheSqlServer(builder.Configuration.GetConnectionString("Cache")!);
```
<!-- endSnippet -->

With options:

<!-- snippet: sqlserver-options -->
```cs
builder.Services.AddMethodCacheSqlServer(options =>
{
    options.ConnectionString = connectionString;
    options.SchemaName = "cache";
    options.BackplanePollingInterval = TimeSpan.FromMilliseconds(500);
});
```
<!-- endSnippet -->

## Tables

By default the schema and two tables are created when the application starts, if they do not exist. Startup waits at most three seconds for that; if SQL Server is down, creation is retried in the background and methods run uncached until it succeeds.

- `MethodCache` holds the entries, in the layout `Microsoft.Extensions.Caching.SqlServer` uses (the same as `dotnet sql-cache create`).
- `MethodCacheInvalidations` holds recent invalidations for the backplane. Its `Scope` is a 32-byte SHA-256 hash
  of the exact UTF-16 little-endian `KeyPrefix`; publishing, polling and retention cleanup all use that scope.
  Prefixes that differ by casing or Unicode normalization stay distinct regardless of SQL collation.

If the application's login may not create tables, set `CreateTablesIfMissing = false` and create them in a migration instead.

Upgrading a table from an earlier build adds a nullable `Scope binary(32)` column and the indexes
`Index_Scope_Id (Scope, Id)` and `Index_Scope_CreatedAt (Scope, CreatedAt)`. With
`CreateTablesIfMissing = false`, add these in your migration before deploying. Existing unscoped rows are
ignored and can be removed after all instances have stopped using the old schema. Upgrade every instance
sharing the table together and restart them with empty in-memory caches; mixed old and new backplanes do
not share the same isolation contract. New tables require a non-null scope.

## How it works

- Entries are stored through MethodCache's own `Microsoft.Extensions.Caching.SqlServer` cache, which also removes expired rows. Your app's `IDistributedCache` is left alone.
- Values are JSON written with System.Text.Json; add `MethodCache.Serialization.MessagePack` and call `AddMethodCacheMessagePack()` for MessagePack.
- MethodCache reads SQL Server on an in-memory miss and writes new results to both.
- Invalidating a key removes its row. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag tables are needed.
- Both also add a row to the invalidations table. Other instances read new rows every `BackplanePollingInterval` and drop their in-memory copies.
- A message is acknowledged only after its subscriber completes successfully. Failed messages remain pending
  for the next poll, including late rows discovered through identity gaps; a failed recovery clear is retried.
  A subscriber cancellation does not stop polling unless the backplane itself is stopping.
- Rows older than `BackplaneRetention` are deleted from the invalidations table.
- An instance that couldn't read the invalidations table for half of `BackplaneRetention` may have missed invalidations, so it clears its in-memory cache.
- Pattern invalidation (`InvalidateByTagPatternAsync`) is not supported with a distributed cache and throws `NotSupportedException`.

## When SQL Server is down

MethodCache keeps answering from memory and your methods; SQL Server never fails or stalls a call.

- Every SQL Server call is capped at `OperationTimeout` (1 second).
- After a failure or timeout, MethodCache skips SQL Server for `CircuitBreakerDuration` (10 seconds), then one call checks whether it is back. The first failure and the recovery are logged.
- Invalidations that SQL Server doesn't accept are queued and retried until it does. They never throw. Until they are stored, this instance doesn't read entries from SQL Server, so an invalidated value can't come back.
- A value set directly while SQL Server is down stays in this instance's memory; the value it replaced is removed from SQL Server and the other instances once SQL Server is back.
- `ClockSkewTolerance` (2 seconds) covers servers whose clocks differ: a tag invalidation also applies to entries created up to that long after it.

<!-- snippet: distributed-outage-options -->
```cs
builder.Services.Configure<DistributedCacheOptions>(options =>
{
    options.OperationTimeout = TimeSpan.FromMilliseconds(500);
    options.CircuitBreakerDuration = TimeSpan.FromSeconds(30);
});
```
<!-- endSnippet -->

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionString` | (required) | SQL Server connection string |
| `SchemaName` | `dbo` | Schema of both tables |
| `TableName` | `MethodCache` | Table for cache entries |
| `InvalidationsTableName` | `MethodCacheInvalidations` | Table the backplane uses |
| `KeyPrefix` | `methodcache:` | Prefix for cache keys and the isolated backplane scope |
| `CreateTablesIfMissing` | `true` | Create the schema and tables at startup |
| `EnableBackplane` | `true` | Share invalidations with other instances |
| `BackplanePollingInterval` | 1 second | How often each instance reads new invalidations |
| `BackplaneRetention` | 1 hour | How long invalidations are kept; at least one minute |
