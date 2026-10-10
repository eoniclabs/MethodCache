# MethodCache.Providers.SqlServer

SQL Server as MethodCache's distributed cache. Results are shared between application instances, and invalidations reach every instance's in-memory cache through a table that each instance polls.

## Usage

```csharp
builder.Services.AddMethodCache();
builder.Services.AddMethodCacheSqlServer(builder.Configuration.GetConnectionString("Cache")!);
```

With options:

```csharp
builder.Services.AddMethodCacheSqlServer(options =>
{
    options.ConnectionString = connectionString;
    options.SchemaName = "cache";
    options.BackplanePollingInterval = TimeSpan.FromMilliseconds(500);
});
```

## Tables

By default the schema and two tables are created when the application starts, if they do not exist:

- `MethodCache` holds the entries, in the layout `Microsoft.Extensions.Caching.SqlServer` uses (the same as `dotnet sql-cache create`).
- `MethodCacheInvalidations` holds recent invalidations for the backplane.

If the application's login may not create tables, set `CreateTablesIfMissing = false` and create them in a migration instead.

## How it works

- Entries are stored through `Microsoft.Extensions.Caching.SqlServer`, which also removes expired rows.
- MethodCache reads SQL Server on an in-memory miss and writes new results to both.
- Invalidating a key removes its row. Invalidating a tag stores the time of the invalidation, and entries with that tag written before it are treated as missing, so no tag tables are needed.
- Both also add a row to the invalidations table. Other instances read new rows every `BackplanePollingInterval` and drop their in-memory copies.
- Rows older than `BackplaneRetention` are deleted from the invalidations table.
- Pattern invalidation (`InvalidateByTagPatternAsync`) is not supported with a distributed cache and throws `NotSupportedException`.

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `ConnectionString` | (required) | SQL Server connection string |
| `SchemaName` | `dbo` | Schema of both tables |
| `TableName` | `MethodCache` | Table for cache entries |
| `InvalidationsTableName` | `MethodCacheInvalidations` | Table the backplane uses |
| `KeyPrefix` | `methodcache:` | Prefix for every key MethodCache writes |
| `CreateTablesIfMissing` | `true` | Create the schema and tables at startup |
| `EnableBackplane` | `true` | Share invalidations with other instances |
| `BackplanePollingInterval` | 1 second | How often each instance reads new invalidations |
| `BackplaneRetention` | 1 hour | How long invalidations are kept |
