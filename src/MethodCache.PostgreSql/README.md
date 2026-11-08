# MethodCache.PostgreSql

PostgreSQL as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache through a table, announced at once with `LISTEN`/`NOTIFY`.

Install it at the same version as `MethodCache`.

## Why this package

- **One shared cache for every instance.** Results live in each instance's memory and in PostgreSQL, so a result one instance computed is a hit for the others, and survives restarts.
- **Invalidation reaches every instance at once.** Invalidating a key or tag removes it from PostgreSQL, and a notification tells every other instance to drop its in-memory copy, typically within milliseconds. A poll every second catches anything a notification missed.
- **Tags work across instances**, without tag tables: invalidating a tag stores when it happened, stored atomically so a late, older invalidation never undoes a newer one.
- **No new infrastructure.** If you already run PostgreSQL, the schema and tables are created when the application starts.
- **Built for speed.** One round trip per read and per new value, prepared statements, new cache values and backplane messages committed without waiting for the disk, and values up to about 8 KB kept in the row. Small values read about as fast as from Redis ([measurements](#performance)).
- **PostgreSQL never breaks your app.** Calls are capped at one second, a circuit breaker skips PostgreSQL while it is down, and invalidations are queued until it is back.

## PostgreSQL, SQL Server or Redis?

| | PostgreSQL | SQL Server | Redis |
|---|---|---|---|
| Infrastructure | The PostgreSQL you already have | The SQL Server you already have | A Redis server |
| Other instances drop their in-memory copy | At once (`NOTIFY`), with a poll every second as a safety net | On their next poll (every second by default) | At once (pub/sub) |
| Load when idle | One small query per instance and second | One small query per instance and second | None |
| Each distributed cache read or write | One prepared statement (two to replace a live value) | A SQL Server query | A Redis command |

Choose PostgreSQL when you already run it; choose [Redis](https://github.com/eoniclabs/MethodCache/blob/main/src/MethodCache.Redis/README.md) for the lowest latency with large values or a very high write rate, or when you would rather keep cache traffic away from your database.

Requires PostgreSQL 13 or later. Provider, atomic metadata and serializer contracts are internal.

## Usage

<!-- snippet: postgresql-basic -->
```cs
builder.Services.AddMethodCache(mc => mc.UsePostgreSql(builder.Configuration.GetConnectionString("Cache")!));
```
<!-- endSnippet -->

With options:

<!-- snippet: postgresql-options -->
```cs
builder.Services.AddMethodCache(mc => mc.UsePostgreSql(options =>
{
    options.ConnectionString = connectionString;
    options.SchemaName = "cache";
    options.CreateTablesIfMissing = false;   // tables created by migrations/0001-create-schema.sql
}));
```
<!-- endSnippet -->

MethodCache keeps its own connection pool (an `NpgsqlDataSource`), so your app's data sources, `DbContext` and `IDistributedCache` are left alone.

### Invalidations only

When each process should keep its own values and only hear about invalidations, such as a worker that changes data an
API caches, set `StoreValuesByDefault = false` in every process. Values stay in each process's memory, for cached methods and
manual calls alike, only those that set `Distributed` to `true` are written to PostgreSQL, and an invalidation in one process reaches all of them through `NOTIFY`:

<!-- snippet: distributed-invalidations-only -->
```cs
builder.Services.AddMethodCache(mc => mc
    .UsePostgreSql(connectionString)
    .Distributed(o => o.StoreValuesByDefault = false));   // values stay in memory; invalidations reach every process
```
<!-- endSnippet -->

The tables are the same as for a full distributed cache. See [Invalidations only](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#invalidations-only).

### Passwordless login

A database that logs in with a token instead of a password, such as Azure Database for PostgreSQL with Microsoft Entra ID or Amazon RDS with IAM, gets the token through `ConfigureDataSource`. MethodCache calls it for its connection pool and for the connection that listens for invalidations:

<!-- snippet: postgresql-passwordless -->
```cs
builder.Services.AddMethodCache(mc => mc.UsePostgreSql(options =>
{
    // No password: each new connection logs in with a token, refreshed before it expires
    options.ConnectionString = "Host=mydb.postgres.database.azure.com;Database=app;Username=app-identity;SSL Mode=Require";
    options.ConfigureDataSource = dataSource => dataSource.UsePeriodicPasswordProvider(
        (_, cancellationToken) => getAccessToken(cancellationToken),
        successRefreshInterval: TimeSpan.FromMinutes(55),
        failureRefreshInterval: TimeSpan.FromSeconds(5));
}));
```
<!-- endSnippet -->

With Azure.Identity, `getAccessToken` can be `async ct => (await credential.GetTokenAsync(new(["https://ossrdbms-aad.database.windows.net/.default"]), ct)).Token`. `ConfigureDataSource` also sets client certificates or a custom server certificate check, anything an `NpgsqlDataSourceBuilder` takes. MethodCache then sets again the few settings it relies on, such as no pooling for the listening connection.

## Tables

By default the schema and four tables are created when the application starts, if they do not exist. Startup waits at most three seconds for that; if PostgreSQL is down, creation is retried in the background and methods run uncached until it succeeds. Instances that start together create the tables once, under an advisory lock.

- `methodcache` holds the entries: a 32-byte id, the value and its expiry time.
- `methodcache_schema` contains the schema version (currently 1). Unknown versions are rejected.
- `methodcache_invalidations` holds recent invalidations for the backplane. Applications that share it are kept apart by `KeyPrefix`.
- `methodcache_invalidations_cursor` holds one row per `KeyPrefix` that hands out the invalidations' ids in commit order and keeps the longest `BackplaneRetention` of its instances. It is unlogged: recovery from a crash empties it, which tells every instance that the last invalidations may be lost.

If the application's login may not create tables, run the package's [`migrations/0001-create-schema.sql`](https://github.com/eoniclabs/MethodCache/blob/main/src/MethodCache.PostgreSql/migrations/0001-create-schema.sql) once as a role that may, grant the login `SELECT, INSERT, UPDATE, DELETE` on the four tables, and set `CreateTablesIfMissing = false`. The script is written for the default names; replace them throughout if you configured others.

Existing tables are checked at startup: an incompatible schema is logged as an error and never converted. An error found during the startup wait also fails startup.

## Connection poolers

Cache reads and writes work through any pooler, PgBouncer in transaction mode included. Notifications need a session: each instance holds one extra, unpooled connection for `LISTEN`. Behind PgBouncer in transaction mode a `LISTEN` doesn't survive the transaction, so other instances' invalidations arrive by polling instead, within `BackplanePollingInterval`; MethodCache logs one warning when it sees this. To get them at once, use a session-mode pool or a direct connection string for MethodCache.

## When PostgreSQL is down

MethodCache keeps answering from memory and your methods; PostgreSQL never fails or stalls a call.

- Every PostgreSQL call is capped at `OperationTimeout` (1 second).
- After a failure or timeout, MethodCache skips PostgreSQL for `CircuitBreakerDuration` (10 seconds), then one call checks whether it is back. The first failure and the recovery are logged.
- Invalidations that PostgreSQL doesn't accept are queued and retried until it does. They never throw. Until they are stored, this instance doesn't read entries from PostgreSQL, so an invalidated value can't come back.
- A value set directly while PostgreSQL is down stays in this instance's memory; the value it replaced is removed from PostgreSQL and the other instances once PostgreSQL is back.
- The `LISTEN` connection reconnects with a delay that doubles from one second to at most 30 seconds; until then, invalidations arrive by polling.
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
| `ConnectionString` | (required) | PostgreSQL connection string, in the format Npgsql reads |
| `SchemaName` | `public` | Schema of the four tables |
| `TableName` | `methodcache` | Table for cache entries; the schema-version table is named after it |
| `InvalidationsTableName` | `methodcache_invalidations` | Table the backplane uses; the cursor table is named after it |
| `ConnectionStringFactory` | none | Resolves a connection string once from DI at binding |
| `ConfigureDataSource` | none | Configures MethodCache's `NpgsqlDataSourceBuilder`s: a password provider for [passwordless login](#passwordless-login), client certificates or a server certificate check |
| `CreateTablesIfMissing` | `true` | Create the schema and tables at startup |
| `EnableBackplane` | `true` | Share invalidations with other instances |
| `BackplanePollingInterval` | 1 second | How often each instance reads new invalidations when no notification arrived |
| `BackplaneRetention` | 1 hour | How long invalidations are kept; at least one minute. Instances that share a `KeyPrefix` keep them as long as the longest retention any of them has used |

Names are used exactly as written (quoted), at most 63 bytes, less the suffix of the tables and indexes named after them.

## Performance

Medians on one machine (4 vCPU, PostgreSQL 17, Redis 7.4 and SQL Server 2022 in containers next to the app):

| | PostgreSQL | Redis | SQL Server |
|---|---:|---:|---:|
| Read, 256 B | 0.30 ms | 0.27 ms | 1.09 ms |
| Write a new key, 256 B | 0.32 ms | 0.30 ms | 2.61 ms |
| Replace a live value, 256 B | 1.08 ms | 0.29 ms | 2.60 ms |
| `[Cache]` method, hit in the distributed cache | 0.71 ms | 0.64 ms | 1.65 ms |
| `[Cache]` method, miss | 1.85 ms | 1.09 ms | 6.29 ms |
| Tag invalidation until another instance runs the method again | 9.3 ms | 2.6 ms | 984 ms |

Replacing a live value waits for the disk, so a crash can't bring back the value it replaced; a miss writes a new
key, which doesn't. Redis stays ahead with large values and many concurrent callers. See
[benchmarks](https://github.com/eoniclabs/MethodCache/blob/main/docs/benchmarks.md#distributed-cache-providers) for
value sizes, throughput and how they were measured.

## How it works

- Every operation is one statement, one round trip, except replacing a live value (below). Statements are prepared automatically on each connection (`Max Auto Prepare=32`), a connection that goes back to the pool isn't reset (`No Reset On Close=true`: MethodCache changes no session state), and connections are named `MethodCache` in `pg_stat_activity`. A setting your connection string makes itself is kept, except one: cache commands never join your `TransactionScope` (`Enlist=false`), so a scope that rolls back doesn't undo cache writes or invalidations.
- New cache values (and values that replace an expired one) and backplane messages are written with `synchronous_commit = off`, for that statement only: PostgreSQL acknowledges the write before flushing it to disk. If the server itself crashes, the last fraction of a second of them can be lost. A lost new value is only a miss. A lost message would leave other instances' in-memory copies, but recovery also empties the unlogged cursor table, so every instance sees a new cursor and clears its in-memory cache. A value that replaces a live one takes a second statement, committed as the server's `synchronous_commit` says, like invalidations, which keep the shared cache correct: a crash never brings back a value that was replaced or invalidated.
- Values are stored as `bytea`, not `jsonb`: they are already serialized (JSON or MessagePack) and never queried, so PostgreSQL stores the bytes without parsing them. Values up to about 8 KB stay in the row, so a read needs no second lookup in the TOAST table; values from about 2 KB are compressed, with lz4 where the server supports it (PostgreSQL 14 and later).
- Values are JSON written with System.Text.Json (public fields included); a method whose results don't read back unchanged stays in memory only, with one warning ([Serialization](https://github.com/eoniclabs/MethodCache/blob/main/docs/features/distributed-cache.md#serialization)). Add `MethodCache.Serialization.MessagePack` and call `UseMessagePack()` on the `AddMethodCache` builder for MessagePack.
- Keys are stored as the SHA-256 of their exact UTF-16 code units, so keys PostgreSQL text can't hold (NUL characters, lone surrogates) and keys of any length get a fixed-size id.
- Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag tables are needed. A read checks the value and its invalidation in one query.
- Both also add a row to the invalidations table and `NOTIFY` a channel named after the `KeyPrefix` and table. The notification carries only the sender's id: the table is the truth, and the notification only tells other instances to read it now instead of at their next poll.
- The cursor row hands out ids in commit order, so a reader never sees a later id before an earlier one commits, and needs no gap tracking.
- A message that fails to apply is retried on the next poll. If it still fails after five polls, the instance clears its in-memory cache in its place and moves on, so one bad message can't hold back the ones after it.
- An instance that couldn't read the invalidations table, or catch up with it, for half of `BackplaneRetention` (since it last did, or since it started), or that sees a new cursor (after a crash, a failover or a restore) or the cursor go back, may have missed invalidations, so it clears its in-memory cache.
- Every instance deletes expired entries every 5 minutes, and its own prefix's invalidations older than the longest `BackplaneRetention` of that prefix every minute, in small batches with `FOR UPDATE SKIP LOCKED`, so instances never wait for each other. Rows of a prefix that no running application uses any more are never deleted; delete them by hand.
- `DistributedCacheOptions.KeyPrefix` keeps applications that share the tables apart. The first instance that uses a
  prefix fixes its minimum `TagRetention`; a shorter setting fails at startup.

The exact behavior is in the
[distributed cache reference](https://github.com/eoniclabs/MethodCache/blob/main/docs/reference/distributed.md).
