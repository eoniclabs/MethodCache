# MethodCache.SqlServer

SQL Server as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache through a table that each instance polls.

Install it at the same version as `MethodCache`.

## Why this package

- **One shared cache for every instance.** Results live in each instance's memory and in SQL Server, so a result one instance computed is a hit for the others, and survives restarts.
- **Invalidation reaches every instance.** Invalidating a key or tag removes it from SQL Server, and every other instance drops its in-memory copy on its next poll (every second by default).
- **Tags work across instances**, without tag tables: invalidating a tag stores when it happened, stored atomically so a late, older invalidation never undoes a newer one.
- **No new infrastructure.** If you already run SQL Server, the schema and tables are created when the application starts.
- **SQL Server never breaks your app.** Calls are capped at one second, a circuit breaker skips SQL Server while it is down, and invalidations are queued until it is back.

Microsoft's `AddDistributedSqlServerCache()` (`SqlServerCache`) can't be used for this: it has no atomic invalidation timestamps, so MethodCache rejects it at startup, and it has no way to tell other instances to drop their in-memory copies. This package uses the same table layout, adds both, and keeps its own cache instance so your app's `IDistributedCache` is left alone.

## SQL Server or Redis?

| | SQL Server | Redis |
|---|---|---|
| Infrastructure | The SQL Server you already have | A Redis server |
| Other instances drop their in-memory copy | On their next poll (every second by default) | At once (pub/sub) |
| Load when idle | Every instance polls the invalidations table | None |
| Each distributed cache read or write | A SQL Server query | A Redis command |

Choose SQL Server when you already run it and a delay of about a second before other instances see an invalidation is fine; choose [Redis](https://github.com/eoniclabs/MethodCache/blob/main/src/MethodCache.Redis/README.md) when invalidations must reach other instances at once, or many instances would poll the database.

Only Microsoft SQL Server is supported: the package uses T-SQL locking hints and `Microsoft.Data.SqlClient`. For PostgreSQL, use [MethodCache.PostgreSql](https://github.com/eoniclabs/MethodCache/blob/main/src/MethodCache.PostgreSql/README.md), whose backplane also reaches other instances at once; MySQL and other databases are not supported.

## Usage

<!-- snippet: sqlserver-basic -->
```cs
builder.Services.AddMethodCache(mc => mc.UseSqlServer(builder.Configuration.GetConnectionString("Cache")!));
```
<!-- endSnippet -->

With options:

<!-- snippet: sqlserver-options -->
```cs
builder.Services.AddMethodCache(mc => mc.UseSqlServer(options =>
{
    options.ConnectionString = connectionString;
    options.SchemaName = "cache";
    options.BackplanePollingInterval = TimeSpan.FromMilliseconds(500);
}));
```
<!-- endSnippet -->

### Invalidations only

When each process should keep its own values and only hear about invalidations, such as a worker that changes data an
API caches, turn the distributed cache off for every method, in every process. Values stay in each process's memory,
none are written to SQL Server, and an invalidation in one process reaches all of them on their next poll:

<!-- snippet: sqlserver-invalidations-only -->
```cs
builder.Services.AddMethodCache(mc => mc
    .UseSqlServer(builder.Configuration.GetConnectionString("Cache")!)
    .Defaults(o => o.WithDistributed(false)));   // values stay in memory; invalidations reach every process
```
<!-- endSnippet -->

The tables are the same as for a full distributed cache. See [Invalidations only](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#invalidations-only).

## Tables

By default the schema and three tables are created when the application starts, if they do not exist. Startup waits at most three seconds for that; if SQL Server is down, creation is retried in the background and methods run uncached until it succeeds.

- `MethodCache` holds the entries, in the layout `Microsoft.Extensions.Caching.SqlServer` uses (the same as `dotnet sql-cache create`).
- `MethodCacheSchema` contains the schema version (currently 1). Existing unversioned or unknown schemas are rejected.
- `MethodCacheInvalidations` holds recent invalidations for the backplane. Applications that share it are kept
  apart by `KeyPrefix` (compared exactly, whatever the SQL collation).

If the application's login may not create tables, set `CreateTablesIfMissing = false` and create them in a migration instead.

### Required backplane schema

The invalidations table needs a `Scope binary(32) NOT NULL` column and the indexes `Index_Scope_Id (Scope, Id)` and
`Index_Scope_CreatedAt (Scope, CreatedAt)`. MethodCache refuses a table without them instead of changing it, and logs
the error; an error found during the startup wait also fails startup. With `CreateTablesIfMissing = false`, create the
current schema yourself.

## When SQL Server is down

MethodCache keeps answering from memory and your methods; SQL Server never fails or stalls a call.

- Every SQL Server call is capped at `OperationTimeout` (1 second).
- After a failure or timeout, MethodCache skips SQL Server for `CircuitBreakerDuration` (10 seconds), then one call checks whether it is back. The first failure and the recovery are logged.
- Invalidations that SQL Server doesn't accept are queued and retried until it does. They never throw. Until they are stored, this instance doesn't read entries from SQL Server, so an invalidated value can't come back.
- A value set directly while SQL Server is down stays in this instance's memory; the value it replaced is removed from SQL Server and the other instances once SQL Server is back.
- `ClockSkewTolerance` (2 seconds) covers servers whose clocks differ: a tag invalidation also applies to entries created up to that long after it.

<!-- snippet: distributed-outage-options -->
```cs
builder.Services.AddMethodCache(mc => mc.Distributed(options =>
{
    options.OperationTimeout = TimeSpan.FromMilliseconds(500);
    options.CircuitBreakerDuration = TimeSpan.FromSeconds(30);
}));
```
<!-- endSnippet -->

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionString` | (required) | SQL Server connection string |
| `SchemaName` | `dbo` | Schema of the cache, invalidations and schema-version tables |
| `TableName` | `MethodCache` | Table for cache entries |
| `InvalidationsTableName` | `MethodCacheInvalidations` | Table the backplane uses |
| `ConnectionStringFactory` | none | Resolves a connection string once from DI at binding |
| `CreateTablesIfMissing` | `true` | Create the schema and tables at startup |
| `EnableBackplane` | `true` | Share invalidations with other instances |
| `BackplanePollingInterval` | 1 second | How often each instance reads new invalidations |
| `BackplaneRetention` | 1 hour | How long invalidations are kept; at least one minute. Instances that share a `KeyPrefix` keep them as long as the longest retention any of them has used |

## How it works

- Cache commands never join your `TransactionScope`: MethodCache sets `Enlist=false` on its connection string, so a
  scope that rolls back doesn't undo cache writes or invalidations, and never becomes a distributed transaction.
- MethodCache reads SQL Server on an in-memory miss and writes new results to both.
- Expired rows are removed by one `DELETE` every 5 minutes on each instance. With a very large table, keep an index on
  `ExpiresAtTime` (`dotnet sql-cache create` and `CreateTablesIfMissing` add one).
- Values are JSON by default. A method whose results don't read back unchanged stays in memory only, with one warning
  ([Serialization](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#serialization)).
  For MessagePack, add `MethodCache.Serialization.MessagePack` and call `UseMessagePack()`.
- Keys are stored as a SHA-256 hash of the exact key, so keys that SQL collation would treat as equal (casing,
  trailing spaces) stay distinct, and keys of any length fit.
- Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as
  missing, so no tag tables are needed. An older invalidation that arrives late never undoes a newer one.
- Every invalidation also adds a row to the invalidations table. Other instances read new rows every
  `BackplanePollingInterval` and drop their in-memory copies. A long-running transaction elsewhere never holds back
  newer invalidations.
- An instance that couldn't read the invalidations table for half of `BackplaneRetention` may have missed
  invalidations, so it clears its in-memory cache.
- Each instance deletes its prefix's old invalidation rows every minute. Rows are kept for the longest
  `BackplaneRetention` any instance of the prefix has used, so a rolling deployment never deletes rows another instance
  still needs. Rows of a prefix that no application uses any more are never deleted; delete them by hand.
- `DistributedCacheOptions.KeyPrefix` keeps applications that share the tables apart. The first instance that uses a
  prefix fixes its minimum `TagRetention`; a shorter setting fails at startup.

The exact behavior is in the
[distributed cache reference](https://github.com/eoniclabs/MethodCache/blob/main/docs/reference/distributed.md).

## Upgrading from before 1.0

An invalidations table from before 1.0 has no schema version, and MethodCache refuses it. Stop every old instance,
back up the database and run
[`0001-version-existing-schema.sql`](https://github.com/eoniclabs/MethodCache/blob/main/src/MethodCache.SqlServer/migrations/0001-version-existing-schema.sql)
before starting the new version.
