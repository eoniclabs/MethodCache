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
- `MethodCacheInvalidations` holds recent invalidations for the backplane. Applications that share it are kept
  apart by `KeyPrefix` (compared exactly, whatever the SQL collation).

If the application's login may not create tables, set `CreateTablesIfMissing = false` and create them in a migration instead.

### Upgrading from an earlier build

The invalidations table gets a nullable `Scope binary(32)` column and the indexes `Index_Scope_Id (Scope, Id)` and
`Index_Scope_CreatedAt (Scope, CreatedAt)`; with `CreateTablesIfMissing = false`, add them in your migration before
deploying. Rows without a scope are ignored, and each instance deletes them once they are older than its
`BackplaneRetention`, as the old build did. Cache entries
written by an earlier build become misses and expire normally. Upgrade every instance that shares the tables
together, and restart them with empty in-memory caches.

## How it works

- Entries are stored through MethodCache's own `Microsoft.Extensions.Caching.SqlServer` cache, which also removes expired rows. Your app's `IDistributedCache` is left alone.
- Values are JSON written with System.Text.Json; add `MethodCache.Serialization.MessagePack` and call `AddMethodCacheMessagePack()` for MessagePack.
- MethodCache reads SQL Server on an in-memory miss and writes new results to both.
- Invalidation times are stored atomically, so an older invalidation that arrives late never undoes a newer one.
- Keys are stored hashed, so keys that SQL collation would treat as equal (casing, trailing spaces, Unicode forms) stay distinct. Keys and tags of any length get a fixed-size ID.
- Invalidating a key removes its row. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag tables are needed.
- Both also add a row to the invalidations table. Other instances read new rows every `BackplanePollingInterval` and drop their in-memory copies.
- A message that fails to apply is retried on the next poll. If it still fails after five polls, the instance clears its in-memory cache in its place and moves on, so one bad message can't hold back the ones after it.
- Each instance deletes its own prefix's rows older than its `BackplaneRetention` from the invalidations table, along with rows an earlier build wrote without a prefix. Rows of a prefix that no running application uses any more are never deleted; delete them by hand.
- Identity values are handed out before a transaction commits, so a row can appear after later ones were read. The ids an instance skipped are checked again for a few seconds; ids taken by other prefixes' rows in a shared table are recognized as committed and are not checked again.
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
| `KeyPrefix` | `methodcache:` | Prefix for every key MethodCache writes; also keeps backplanes that share the tables apart |
| `CreateTablesIfMissing` | `true` | Create the schema and tables at startup |
| `EnableBackplane` | `true` | Share invalidations with other instances |
| `BackplanePollingInterval` | 1 second | How often each instance reads new invalidations |
| `BackplaneRetention` | 1 hour | How long invalidations are kept; at least one minute |

Distributed tags and backplane keys/tags preserve exact UTF-16 code units, including lone surrogates. Envelope version 4 and backplane frames use lossless representations; older envelopes are cache misses. Deploy the same MethodCache version across nodes sharing a cache and backplane.
