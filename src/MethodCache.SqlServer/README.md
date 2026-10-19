# MethodCache.SqlServer

Requires .NET 10 and the .NET 10 SDK; use the same package version as `MethodCache`.

Install this package and `MethodCache` at the exact same version. Integration packages use Core internals;
do not override their exact Core dependency. Our package smoke tests promote NU1608 to a restore error.

SQL Server as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache through a table that each instance polls.

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

Only Microsoft SQL Server is supported: the package uses T-SQL locking hints and `Microsoft.Data.SqlClient`. PostgreSQL, MySQL and other databases are not supported. Provider, atomic metadata and serializer contracts are internal.

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

## Tables

By default the schema and three tables are created when the application starts, if they do not exist. Startup waits at most three seconds for that; if SQL Server is down, creation is retried in the background and methods run uncached until it succeeds.

- `MethodCache` holds the entries, in the layout `Microsoft.Extensions.Caching.SqlServer` uses (the same as `dotnet sql-cache create`).
- `MethodCacheSchema` contains the schema version (currently 1). Existing unversioned or unknown schemas are rejected.
- `MethodCacheInvalidations` holds recent invalidations for the backplane. Applications that share it are kept
  apart by `KeyPrefix` (compared exactly, whatever the SQL collation).

If the application's login may not create tables, set `CreateTablesIfMissing = false` and create them in a migration instead.

### Required backplane schema

The invalidations table requires `Scope binary(32) NOT NULL` and the indexes `Index_Scope_Id (Scope, Id)` and
`Index_Scope_CreatedAt (Scope, CreatedAt)`. Automatic initialization rejects incompatible scope columns with a
logged schema error and does not convert tables or rewrite their rows. An error detected during the bounded
startup wait also fails startup. With `CreateTablesIfMissing = false`, provision the current schema yourself.

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
| `BackplaneRetention` | 1 hour | How long invalidations are kept; at least one minute |

## Internals

- Entries are stored through MethodCache's own `Microsoft.Extensions.Caching.SqlServer` cache, which also removes expired rows: every instance runs one `DELETE` of the rows that expired every 5 minutes (the shortest interval that cache allows), so each delete stays small. With a very large table, keep an index on `ExpiresAtTime` (`dotnet sql-cache create` and `CreateTablesIfMissing` add one). Your app's `IDistributedCache` is left alone.
- Values are JSON written with System.Text.Json (public fields included); a method whose results don't read back unchanged stays in memory only, with one warning ([Serialization](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#serialization)). Add `MethodCache.Serialization.MessagePack` and call `UseMessagePack()` on the `AddMethodCache` builder for MessagePack.
- MethodCache reads SQL Server on an in-memory miss and writes new results to both.
- Invalidation times are stored atomically, so an older invalidation that arrives late never undoes a newer one.
- Keys are stored as `mc:` followed by a SHA-256 hash of the exact UTF-16 key, so keys that SQL collation would treat as equal (casing, trailing spaces, NULs, Unicode forms) stay distinct. Keys and tags of any length get a fixed-size ID.
- Invalidating a key writes a format-independent logical tombstone and removes the current physical row. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag tables are needed.
- Both also add a row to the invalidations table. Other instances read new rows every `BackplanePollingInterval` and drop their in-memory copies.
- A message that fails to apply is retried on the next poll. If it still fails after five polls, the instance clears its in-memory cache in its place and moves on, so one bad message can't hold back the ones after it.
- Each instance deletes its own prefix's rows older than its `BackplaneRetention` from the invalidations table. Rows of a prefix that no running application uses any more are never deleted; delete them by hand.
- Identity values are handed out before a transaction commits, so a row can appear after later ones were read. The ids an instance skipped are checked again without blocking. After five seconds, a separate `NOWAIT` locking probe can retire gaps proven absent. Still-locked gaps and timed-out probes are retried without holding back newer invalidations or other committed gap messages; ids taken by other prefixes' rows in a shared table are recognized as committed and are not checked again.
- An instance that couldn't read the invalidations table for half of `BackplaneRetention` may have missed invalidations, so it clears its in-memory cache.
- Invalidation matches exact logical keys and application tags. Use a common tag for a group of entries.

Wire v1 remains stable across 1.x entry formats. Malformed mandatory frames clear local entries and warn once.
Initialized permanent HTTP epochs are read without rewriting their rows. Initialization and retention repair
remain atomic, and each HTTP lookup still reads the shared generation records.
The transport reuses only its latest bounded command-text shape; it never caches generation values.
Reordered and duplicate epoch keys retain their caller positions.

Value and logical tombstone are read in one query. Atomic stamps never shorten retention, and HTTP epochs
are permanent. Configure the shared scope with `DistributedCacheOptions.KeyPrefix`; provider options do not
have a separate prefix. The first node establishes a common retention bound; shorter settings are rejected.

For a known unversioned schema, drain all nodes, back up the database and apply the delivered
[`0001-version-existing-schema.sql`](migrations/0001-version-existing-schema.sql) migration. Unknown schema
versions fail fast; initialization never silently migrates existing tables.
